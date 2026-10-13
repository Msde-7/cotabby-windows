using System.Net.Http;
using Cotabby.App.Emoji;
using Cotabby.App.Macros;
using Cotabby.App.Overlay;
using Cotabby.App.Settings;
using Cotabby.Core.Focus;
using Cotabby.Core.Input;
using Cotabby.Core.Insertion;
using Cotabby.Core.Models;
using Cotabby.Core.Overlay;
using Cotabby.Core.Spelling;
using Cotabby.Core.Suggestions;
using Cotabby.Inference;
using Cotabby.Inference.OpenAICompatible;
using Cotabby.Win32.Clipboard;
using Cotabby.Win32.Focus;
using Cotabby.Win32.Input;
using Cotabby.Win32.Insertion;
using Cotabby.Win32.Power;
using Cotabby.Win32.Security;
using Cotabby.Win32.Spelling;
using Cotabby.Win32.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace Cotabby.App.Hosting;

/// <summary>
/// Composition root. Builds the long-lived object graph once at startup and
/// pushes settings changes into it.
/// </summary>
public sealed class AppHost : IAsyncDisposable
{
    public static string LogPath { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Cotabby", "logs", "cotabby.log");

    /// <summary>Debug logs include typed and suggested text, so they are opt-in.</summary>
    private static readonly bool DebugLogging = Environment.GetEnvironmentVariable("COTABBY_DEBUG_LOG") == "1";

    private readonly ServiceProvider _services;
    private readonly IKeyboardHook _hook;
    private readonly SuggestionEngineRouter _router;
    private readonly SynchronizationContext _ui;
    private readonly ILogger _logger;
    private readonly GhostOverlayWindow _macroPreview;
    private readonly SymSpellCorrector _spelling = new();

    public SuggestionCoordinator Coordinator { get; }
    public LlamaRuntimeManager Runtime { get; }
    public ModelDownloader Downloader { get; }
    public SettingsStore SettingsStore { get; }
    public AppSettings Settings { get; private set; }
    public GhostOverlayWindow Overlay { get; }
    public EmojiPickerController EmojiPicker { get; }
    public MacroController Macros { get; }
    public OpenAICompatibleSuggestionEngine Endpoint { get; }
    public OpenAICompatibleApiClient EndpointClient { get; }
    public IOpenAICompatibleCredentialStore EndpointCredentials { get; }

    /// <summary>Raised on the UI thread after settings were applied or a model was loaded, so open windows and the tray can refresh.</summary>
    public event Action? SettingsApplied;

    public AppHost(
        SynchronizationContext ui, GhostOverlayWindow overlay, GhostOverlayWindow macroPreview,
        EmojiPopupWindow emojiPopup)
    {
        _ui = ui;
        Overlay = overlay;
        _macroPreview = macroPreview;

        var sc = new ServiceCollection();
        sc.AddLogging(b =>
        {
            b.SetMinimumLevel(DebugLogging ? LogLevel.Debug : LogLevel.Information);
            b.AddDebug();
            b.AddProvider(new FileLoggerProvider(LogPath));
        });
        sc.AddSingleton(_ =>
        {
            // GGUF downloads are large, so the timeout is generous.
            var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Cotabby-Windows/0.2 (+https://github.com/Msde-7/cotabby-windows)");
            return http;
        });
        sc.AddSingleton<SettingsStore>();
        sc.AddSingleton<IFocusTracker, UiaFocusTracker>();
        sc.AddSingleton<IKeyboardHook, KeyboardHook>();
        sc.AddSingleton<ClipboardTextReader>();
        sc.AddSingleton<ITextInserter, SendInputTextInserter>();
        sc.AddSingleton<LlamaRuntimeManager>();
        sc.AddSingleton<ModelDownloader>();
        sc.AddSingleton<LlamaSuggestionEngine>();
        sc.AddSingleton<OpenAICompatibleApiClient>(_ => new OpenAICompatibleApiClient());
        sc.AddSingleton<IOpenAICompatibleCredentialStore>(_ => new DpapiOpenAICompatibleCredentialStore());
        _services = sc.BuildServiceProvider();

        SettingsStore = _services.GetRequiredService<SettingsStore>();
        Settings = SettingsStore.Load();
        Runtime = _services.GetRequiredService<LlamaRuntimeManager>();
        Downloader = _services.GetRequiredService<ModelDownloader>();
        EndpointClient = _services.GetRequiredService<OpenAICompatibleApiClient>();
        EndpointCredentials = _services.GetRequiredService<IOpenAICompatibleCredentialStore>();

        var loggers = _services.GetRequiredService<ILoggerFactory>();
        _logger = loggers.CreateLogger("Cotabby.App");
        _hook = _services.GetRequiredService<IKeyboardHook>();
        var focus = _services.GetRequiredService<IFocusTracker>();
        var inserter = _services.GetRequiredService<ITextInserter>();

        Endpoint = new OpenAICompatibleSuggestionEngine(
            EndpointClient, EndpointCredentials, EndpointSettings,
            loggers.CreateLogger<OpenAICompatibleSuggestionEngine>());
        _router = new SuggestionEngineRouter(_services.GetRequiredService<LlamaSuggestionEngine>(), Endpoint);

        IcuWordBreaker.Install();

        // Inline features subscribe to the hook first so they see each key before the coordinator.
        EmojiPicker = new EmojiPickerController(
            _hook, focus, inserter, emojiPopup, overlay.Dispatcher,
            loggers.CreateLogger<EmojiPickerController>());
        Macros = new MacroController(_hook, focus, inserter, macroPreview, loggers.CreateLogger<MacroController>());
        _hook.KeyEvent += OnGlobalToggleKey;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;

        Coordinator = new SuggestionCoordinator(
            _hook, focus, _router, overlay, inserter, new SuggestionWorkController(TimeSpan.Zero),
            loggers.CreateLogger<SuggestionCoordinator>(), ui,
            batterySaverOn: () => PowerStatus.IsBatterySaverOn,
            typos: new TypoCorrectionAdvisor(
                _spelling, new SpellingLanguageResolver(), new WindowsSpellChecker(_spelling)),
            clipboard: _services.GetRequiredService<ClipboardTextReader>());
        Coordinator.InlineCaptureActive = () => EmojiPicker.IsCapturing || Macros.IsCapturing;
        Coordinator.Metrics.Restore(Settings.WordsAccepted);

        ApplySettings();
    }

    private OpenAICompatibleEndpointSettings EndpointSettings => new(
        Settings.EndpointBaseUrl,
        Settings.EndpointModel,
        Settings.EndpointUsesChatApi ? OpenAICompatibleApiMode.ChatCompletions : OpenAICompatibleApiMode.Completions);

    /// <summary>Pushes <see cref="Settings"/> into the running app and saves them.</summary>
    public void PersistSettings()
    {
        ApplySettings();
        Settings.WordsAccepted = Coordinator.Metrics.WordsAccepted;
        SettingsStore.Save(Settings);
    }

    /// <summary>Restores every preference to its default. The chosen model and onboarding state stay.</summary>
    public void ResetSettings()
    {
        Settings = new AppSettings
        {
            ActiveModelId = Settings.ActiveModelId,
            ShowFirstRunWelcome = Settings.ShowFirstRunWelcome,
            WordsAccepted = Coordinator.Metrics.WordsAccepted,
        };
        PersistSettings();
    }

    private void ApplySettings()
    {
        // An expired pause is dropped so the tray stops showing it.
        if (Settings.Pause?.IsActive(DateTimeOffset.Now) == false) Settings.Pause = null;

        Coordinator.Settings = Settings.ToSuggestionSettings();
        _router.UseEndpoint = Settings.Engine == EngineKind.Endpoint;
        Endpoint.UpdateSettings(EndpointSettings);
        EmojiPicker.Enabled = Settings.EmojiPickerEnabled;
        EmojiPicker.CommitKey = Settings.AcceptWord;
        EmojiPicker.PreferredSkinTone = Settings.EmojiSkinTone;
        EmojiPicker.PreferredGender = Settings.EmojiGender;
        Macros.Enabled = Settings.MacrosEnabled;
        Macros.CommitKey = Settings.AcceptWord;
        Overlay.ApplyAppearance(Settings);
        _macroPreview.ApplyAppearance(Settings);
        LaunchAtLogin.Set(Settings.LaunchAtLogin);
        SettingsApplied?.Invoke();
    }

    private void OnGlobalToggleKey(object? sender, KeyEventArgs args)
    {
        if (!args.Event.IsKeyDown || args.Event.IsRepeat || !Settings.GlobalToggle.Matches(args.Event)) return;
        args.Suppress = true;
        _ui.Post(_ =>
        {
            Settings.Enabled = !Settings.Enabled;
            Settings.Pause = null;
            PersistSettings();
        }, null);
    }

    public void LogError(Exception ex, string message) => _logger.LogError(ex, "{Message}", message);

    /// <summary>Loads the selected model if it is already on disk. Never downloads.</summary>
    public async Task<bool> TryLoadCachedAsync(CancellationToken ct)
    {
        var model = ResolveActiveModel();
        if (!ModelDownloader.IsCached(model)) return false;
        return await LoadAsync(model, ct).ConfigureAwait(false);
    }

    /// <summary>Downloads the selected model if needed, then loads it.</summary>
    public async Task<bool> EnsureModelReadyAsync(IProgress<ModelDownloader.Progress>? progress, CancellationToken ct)
    {
        var model = ResolveActiveModel();
        if (!ModelDownloader.IsCached(model))
        {
            await Downloader.DownloadAsync(model, progress, ct).ConfigureAwait(false);
        }
        return await LoadAsync(model, ct).ConfigureAwait(false);
    }

    private async Task<bool> LoadAsync(CotabbyModel model, CancellationToken ct)
    {
        await Runtime.LoadAsync(model, ModelDownloader.LocalPath(model), ct).ConfigureAwait(false);
        // The tray and any open window show which model is loaded.
        _ui.Post(_ => SettingsApplied?.Invoke(), null);
        return Runtime.IsReady;
    }

    private CotabbyModel ResolveActiveModel()
    {
        var model = (Settings.ActiveModelId is { } id ? ModelCatalog.FindById(id) : null) ?? ModelCatalog.All[0];
        Settings.ActiveModelId = model.Id;
        return PowerSourceModel() ?? model;
    }

    /// <summary>The model chosen for the current power source, when that feature is on and the file is present.</summary>
    private CotabbyModel? PowerSourceModel()
    {
        if (!Settings.PowerModelSwitching) return null;
        var id = PowerStatus.IsOnBattery ? Settings.BatteryModelId : Settings.PluggedInModelId;
        return ModelCatalog.FindById(id) is { } model && ModelDownloader.IsCached(model) ? model : null;
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.StatusChange) return;
        _ui.Post(async _ =>
        {
            if (PowerSourceModel() is not { } model || Runtime.ActiveModel?.Id == model.Id) return;
            try
            {
                await TryLoadCachedAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                LogError(ex, "Switching models for the power source failed.");
            }
        }, null);
    }

    public async ValueTask DisposeAsync()
    {
        Settings.WordsAccepted = Coordinator.Metrics.WordsAccepted;
        SettingsStore.Save(Settings);
        _hook.KeyEvent -= OnGlobalToggleKey;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        EmojiPicker.Dispose();
        Macros.Dispose();
        _spelling.Dispose();
        await Coordinator.DisposeAsync().ConfigureAwait(false);
        await Runtime.DisposeAsync().ConfigureAwait(false);
        EndpointClient.Dispose();
        await _services.DisposeAsync().ConfigureAwait(false);
    }
}
