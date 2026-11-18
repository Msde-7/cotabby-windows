using System.Net.Http;
using Cotabby.App.Emoji;
using Cotabby.App.Macros;
using Cotabby.App.Overlay;
using Cotabby.App.Settings;
using Cotabby.App.UI.Controls;
using Cotabby.Core.Context;
using Cotabby.Core.Focus;
using Cotabby.Core.History;
using Cotabby.Core.Input;
using Cotabby.Core.Insertion;
using Cotabby.Core.Metrics;
using Cotabby.Core.Models;
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
using Cotabby.Win32.Visual;
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

    /// <summary>The log file in use, which is cotabby.alt.log while another process, such as a second copy, holds <see cref="LogPath"/>.</summary>
    public string ActiveLogPath => _fileLog.CurrentPath ?? LogPath;

    /// <summary>Debug logs include typed and suggested text, so they are opt-in.</summary>
    private static readonly bool DebugLogging = Environment.GetEnvironmentVariable("COTABBY_DEBUG_LOG") == "1";

    private readonly FileLoggerProvider _fileLog;
    private readonly ILoggerFactory _loggers;
    private readonly HttpClient _http;
    private readonly KeyboardHook _hook;
    private readonly SwitchableFocusTracker _focus;
    private readonly SuggestionEngineRouter _router;
    private readonly SynchronizationContext _ui;
    private readonly ILogger _logger;
    private readonly GhostOverlayWindow _macroPreview;
    private readonly SymSpellCorrector _spelling;
    private readonly BatterySaverMonitor _batterySaver = new();
    private readonly GhostOverlayWindow _overlay;
    private readonly MacroController _macros;
    private readonly OpenAICompatibleSuggestionEngine _endpoint;
    private readonly ScreenContextSource? _screenContext;
    private bool? _launchAtLoginApplied;

    /// <summary>How long settings must stay unchanged before they are written.</summary>
    private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(500);
    private readonly Timer _saveTimer;
    private bool _savePending;

    /// <summary>Fires when a timed pause ends, so focus tracking starts again.</summary>
    private readonly Timer _pauseEndTimer;
    /// <summary>Guards the timers against a change arriving after shutdown disposed them.</summary>
    private readonly object _timerGate = new();
    private bool _disposed;
    /// <summary>Whether focus should be tracked, as last applied. Read by the background switch.</summary>
    private volatile bool _focusTracked = true;

    /// <summary>False when Windows has no OCR language for the user's languages, so screen context cannot run.</summary>
    public bool ScreenContextAvailable { get; }

    public SuggestionCoordinator Coordinator { get; }
    public LlamaRuntimeManager Runtime { get; }
    public ModelDownloader Downloader { get; }
    public SettingsStore SettingsStore { get; }
    public AppSettings Settings { get; private set; }
    public EmojiPickerController EmojiPicker { get; }
    public OpenAICompatibleApiClient EndpointClient { get; }
    public IOpenAICompatibleCredentialStore EndpointCredentials { get; }
    public TypingHistoryStore TypingHistory { get; }
    public RequestLog RequestLog { get; } = new(System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Cotabby", "request-log.json"));

    /// <summary>
    /// The model download the first-run window started, while it runs. Like upstream's, it carries
    /// on after the window closes, so the model list in Settings offers its pause and cancel.
    /// </summary>
    public SetupDownload? RunningSetupDownload
    {
        get => _setupDownload;
        set
        {
            _setupDownload = value;
            SetupDownloadChanged?.Invoke();
        }
    }

    private SetupDownload? _setupDownload;

    /// <summary>
    /// Raised on the UI thread when the first-run window's download starts or ends, so an open
    /// Settings window offers its pause and cancel right away.
    /// </summary>
    public event Action? SetupDownloadChanged;

    /// <summary>A first-run download. A reopened first-run window follows its <see cref="Progress"/> instead of starting another.</summary>
    public sealed record SetupDownload(
        string ModelId, CancellationTokenSource Cancellation, Task Completion, Progress<ModelDownloader.Progress> Progress);

    /// <summary>Raised on the UI thread after settings were applied or a model was loaded, so open windows and the tray can refresh.</summary>
    public event Action? SettingsApplied;

    /// <summary>
    /// Raised on the UI thread with a passing model status, such as loading after a switch back to
    /// the local engine. Null returns to the normal status.
    /// </summary>
    public event Action<string?>? ModelStatusChanged;

    /// <summary>The engine the last apply saw, so a change of engine can load or free the local model.</summary>
    private EngineKind? _appliedEngine;

    public AppHost(
        SynchronizationContext ui, GhostOverlayWindow overlay, GhostOverlayWindow macroPreview,
        EmojiPopupWindow emojiPopup)
    {
        _ui = ui;
        _overlay = overlay;
        _macroPreview = macroPreview;
        _saveTimer = new Timer(_ => _ui.Post(_ => SavePending(), null));
        _pauseEndTimer = new Timer(_ => _ui.Post(_ => ApplyFocusTracking(), null));

        _fileLog = new FileLoggerProvider(LogPath);
        _loggers = LoggerFactory.Create(b =>
        {
            b.SetMinimumLevel(DebugLogging ? LogLevel.Debug : LogLevel.Information);
            b.AddDebug();
            b.AddProvider(_fileLog);
        });
        var loggers = _loggers;
        // GGUF downloads are large, so the timeout is generous.
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Cotabby-Windows/0.2 (+https://github.com/Msde-7/cotabby-windows)");

        SettingsStore = new SettingsStore();
        Settings = SettingsStore.Load();
        // The Run key is written only where it disagrees with the setting, so running another copy,
        // such as a dev build, does not repoint Open at Login to it. Defaults that stand in for a
        // file that could not be read are not the user's choice, so they leave the key alone. An
        // entry whose program is gone, such as a cleaned build folder, is pointed at this copy.
        _launchAtLoginApplied = SettingsStore.CouldNotRead ? Settings.LaunchAtLogin
            : Settings.LaunchAtLogin ? LaunchAtLogin.IsRegisteredToExistingApp()
            : LaunchAtLogin.IsRegistered();
        Runtime = new LlamaRuntimeManager(loggers.CreateLogger<LlamaRuntimeManager>(), loggers);
        Downloader = new ModelDownloader(_http, loggers.CreateLogger<ModelDownloader>());
        EndpointClient = new OpenAICompatibleApiClient();
        EndpointCredentials = new DpapiOpenAICompatibleCredentialStore();

        _spelling = new SymSpellCorrector(logger: loggers.CreateLogger<SymSpellCorrector>());
        _logger = loggers.CreateLogger("Cotabby.App");
        if (SettingsStore.CouldNotRead)
        {
            _logger.LogWarning("{Path} could not be read. Running on the defaults without saving over it.", SettingsStore.Path);
        }
        _hook = new KeyboardHook(loggers.CreateLogger<KeyboardHook>());
        var focus = _focus = new SwitchableFocusTracker(new UiaFocusTracker(loggers.CreateLogger<UiaFocusTracker>()));
        var clipboard = new ClipboardTextReader();
        var inserter = new SendInputTextInserter(loggers.CreateLogger<SendInputTextInserter>(), clipboard, focus);

        _endpoint = new OpenAICompatibleSuggestionEngine(
            EndpointClient, EndpointCredentials, Settings.EndpointSettings,
            loggers.CreateLogger<OpenAICompatibleSuggestionEngine>());
        var llama = new LlamaSuggestionEngine(Runtime, loggers.CreateLogger<LlamaSuggestionEngine>());
        _router = new SuggestionEngineRouter(llama, _endpoint)
        {
            Completed = RecordRequest,
        };
        // Typing history owns its own encrypted archive. Phrase shortcuts answer before the router
        // runs, and the live engine is read per request so the endpoint never gets one.
        TypingHistory = new TypingHistoryStore(
            DpapiTypingHistoryKeyStore.StandardVault(), Settings.TypingHistoryPreferences,
            logger: loggers.CreateLogger<TypingHistoryStore>());
        var historyAwareEngine = new TypingHistoryPhraseEngine(
            _router, TypingHistory, () => _router.UseEndpoint, () => Settings.TypingHistoryEnabled);

        IcuWordBreaker.Install();

        // Inline features subscribe to the hook first so they see each key before the coordinator.
        EmojiPicker = new EmojiPickerController(
            _hook, focus, inserter, emojiPopup, overlay.Dispatcher,
            loggers.CreateLogger<EmojiPickerController>());
        _macros = new MacroController(_hook, focus, inserter, macroPreview, loggers.CreateLogger<MacroController>());
        _hook.KeyEvent += OnGlobalToggleKey;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;

        Coordinator = new SuggestionCoordinator(
            _hook, focus, historyAwareEngine, overlay, inserter, new SuggestionWorkController(TimeSpan.Zero),
            loggers.CreateLogger<SuggestionCoordinator>(), ui,
            batterySaverOn: () => _batterySaver.IsOn,
            typos: new TypoCorrectionAdvisor(
                _spelling, new SpellingLanguageResolver(), new WindowsSpellChecker(_spelling)),
            clipboard: clipboard);
        _batterySaver.Changed += OnBatterySaverChanged;
        Coordinator.InlineCaptureActive = () => EmojiPicker.IsCapturing || _macros.IsCapturing;
        Coordinator.History = TypingHistory;
        // The local model evaluates a newly focused field's prompt ahead of the first key.
        Coordinator.Prewarm = (request, ct) => _router.UseEndpoint ? Task.CompletedTask : llama.PrewarmAsync(request, ct);
        Coordinator.HistoryRecorder = TypingHistory;
        var screenReader = new WindowsScreenTextReader(loggers.CreateLogger<WindowsScreenTextReader>());
        ScreenContextAvailable = screenReader.IsAvailable;
        if (screenReader.IsAvailable)
        {
            _screenContext = new ScreenContextSource(
                screenReader, () => focus.Refresh(), loggers.CreateLogger<ScreenContextSource>());
            Coordinator.ScreenContext = _screenContext;
        }
        EmojiPicker.CanCapture = _macros.CanCapture = Coordinator.IsAvailableIn;
        EmojiPicker.CommitKeyFor = _macros.CommitKeyFor = Coordinator.AcceptWordKeyFor;
        Coordinator.Metrics.Restore(Settings.WordsAccepted);

        ApplySettings();
    }

    /// <summary>Pushes <see cref="Settings"/> into the running app and saves them.</summary>
    public void PersistSettings()
    {
        ApplySettings();
        ScheduleSave();
    }

    /// <summary>
    /// A slider sends a change for every step, so the file is written once the changes have
    /// stopped for a moment, and at exit, instead of on every step.
    /// </summary>
    private void ScheduleSave()
    {
        _savePending = true;
        lock (_timerGate)
        {
            if (!_disposed) _saveTimer.Change(SaveDelay, Timeout.InfiniteTimeSpan);
        }
    }

    private void SavePending()
    {
        if (!_savePending) return;
        _savePending = false;
        try
        {
            SaveSettings();
        }
        catch (Exception ex)
        {
            LogError(ex, "Saving the settings failed.");
        }
    }

    /// <summary>Changes the settings, then applies and saves them.</summary>
    public void Update(Action<AppSettings> change)
    {
        change(Settings);
        PersistSettings();
    }

    /// <summary>Whether suggestions can appear right now, and why not when they cannot.</summary>
    public (bool Ready, string Text) Status()
    {
        if (!Settings.Enabled) return (false, "Cotabby is off");
        if (Settings.Pause?.StatusText(DateTimeOffset.Now) is { } paused) return (false, paused);
        if (Settings.Engine == EngineKind.Endpoint)
        {
            return Settings.EndpointModel.Length > 0
                ? (true, $"Ready · {Settings.EndpointModel}")
                : (false, "No endpoint model selected");
        }
        return Runtime is { IsReady: true, ActiveModel: { } model }
            ? (true, $"Ready · {model.DisplayName}")
            : (false, "No model loaded");
    }

    private void RecordRequest(int latencyMs)
    {
        if (!Settings.PerformanceTracking) return;
        string model = _router.UseEndpoint ? Settings.EndpointModel : Runtime.ActiveModel?.DisplayName ?? "Local model";
        RequestLog.Record(model, latencyMs);
    }

    private void SaveSettings()
    {
        Settings.WordsAccepted = Coordinator.Metrics.WordsAccepted;
        SettingsStore.Save(Settings);
    }

    /// <summary>
    /// Restores every preference to its default. Like upstream, app state that is not a preference
    /// stays: the chosen model, onboarding, the accepted-word count, Open at Login and the typing
    /// history switches with their excluded apps. Emoji and typing history live in their own files.
    /// </summary>
    public void ResetSettings()
    {
        var kept = Settings;
        Settings = new AppSettings
        {
            ActiveModelId = kept.ActiveModelId,
            ShowFirstRunWelcome = kept.ShowFirstRunWelcome,
            WordsAccepted = Coordinator.Metrics.WordsAccepted,
            LaunchAtLogin = kept.LaunchAtLogin,
            TypingHistoryEnabled = kept.TypingHistoryEnabled,
            TypingHistoryRecording = kept.TypingHistoryRecording,
            TypingHistoryExcludedApps = [.. kept.TypingHistoryExcludedApps],
        };
        PersistSettings();
    }

    private void ApplySettings()
    {
        // An expired pause is dropped so the tray stops showing it.
        if (Settings.Pause?.IsActive(DateTimeOffset.Now) == false) Settings.Pause = null;

        Coordinator.Settings = Settings.ToSuggestionSettings();
        ApplyFocusTracking();
        _router.UseEndpoint = Settings.Engine == EngineKind.Endpoint;
        _endpoint.UpdateSettings(Settings.EndpointSettings);
        TypingHistory.ApplyPreferences(Settings.TypingHistoryPreferences);
        // A cold local server would make the first suggestion wait for the model to load.
        if (Settings.Engine == EngineKind.Endpoint) _ = _endpoint.PrewarmAsync();
        EmojiPicker.Enabled = Settings.EmojiPickerEnabled;
        EmojiPicker.PreferredSkinTone = Settings.EmojiSkinTone;
        EmojiPicker.PreferredGender = Settings.EmojiGender;
        _macros.Enabled = Settings.MacrosEnabled;
        _overlay.ApplyAppearance(Settings);
        _macroPreview.ApplyAppearance(Settings);
        // The Run key is written when the switch changes, or at startup when it disagrees with the
        // switch, not on every settings change.
        if (_launchAtLoginApplied != Settings.LaunchAtLogin)
        {
            LaunchAtLogin.Set(Settings.LaunchAtLogin);
            _launchAtLoginApplied = Settings.LaunchAtLogin;
        }
        ApplyEngineResidency();
        SettingsApplied?.Invoke();
    }

    /// <summary>
    /// Stops watching focus while Cotabby is off or paused, when suggestions, emoji and macros are
    /// all off, and starts again when it is turned back on or a timed pause ends. The hook keeps
    /// running, so the global toggle still works.
    /// </summary>
    private void ApplyFocusTracking()
    {
        var now = DateTimeOffset.Now;
        var pause = Settings.Pause is { } p && p.IsActive(now) ? p : null;
        bool track = Settings.Enabled && pause is null;
        // Checked again at least hourly, which also covers a clock change during the pause.
        var pauseEnds = Settings.Enabled && pause?.Until is { } until
            ? TimeSpan.FromTicks(Math.Min((until - now).Ticks, TimeSpan.FromHours(1).Ticks)) + TimeSpan.FromMilliseconds(100)
            : Timeout.InfiniteTimeSpan;
        lock (_timerGate)
        {
            if (!_disposed) _pauseEndTimer.Change(pauseEnds, Timeout.InfiniteTimeSpan);
        }

        if (track == _focusTracked) return;
        _focusTracked = track;
        _logger.LogInformation(track ? "Focus tracking resumed." : "Focus tracking stopped while Cotabby is off.");
        // Off the UI thread, because starting reads the focused field through UI Automation. Each
        // switch applies the latest wish, so switches that finish out of order still end right.
        _ = Task.Run(() =>
        {
            try
            {
                _focus.SetEnabled(_focusTracked);
            }
            catch (Exception ex)
            {
                LogError(ex, "Switching focus tracking failed.");
            }
        });
    }

    /// <summary>
    /// Keeps the local model in memory only while the local engine is selected, as upstream does.
    /// Switching to the endpoint frees it, and switching back loads the selected model when it is
    /// on disk. The first apply leaves the startup load to the app.
    /// </summary>
    private void ApplyEngineResidency()
    {
        var previous = _appliedEngine;
        _appliedEngine = Settings.Engine;
        if (Settings.Engine == EngineKind.Endpoint)
        {
            if (previous == EngineKind.Local) FreeLocalModel();
        }
        else if (previous == EngineKind.Endpoint)
        {
            _ = LoadCachedModelAsync();
        }
        else
        {
            LoadSelectedModelIfChanged();
        }
    }

    private async void FreeLocalModel()
    {
        // A status such as "No model yet" is about the local engine, so it goes with it.
        ModelStatusChanged?.Invoke(null);
        try
        {
            await UnloadModelAsync();
        }
        catch (Exception ex)
        {
            LogError(ex, "Freeing the local model failed.");
        }
    }

    /// <summary>
    /// Loads the selected model if it is already on disk, at startup or after a switch back to the
    /// local engine. Never downloads. The tray shows the load and how it ended. Called on the UI thread.
    /// </summary>
    public Task LoadCachedModelAsync() => LoadShowingStatusAsync(TryLoadCachedAsync);

    /// <summary>
    /// Loads the model picked by hand, which is on disk, as the tray's model menu does. The tray
    /// shows the load and how it ended. Called on the UI thread.
    /// </summary>
    public Task LoadSelectedModelAsync() =>
        LoadShowingStatusAsync(() => EnsureModelReadyAsync(null, CancellationToken.None));

    /// <summary>Runs a model load with its passing status, and logs a failure. Never throws.</summary>
    private async Task LoadShowingStatusAsync(Func<Task<bool>> load)
    {
        ModelStatusChanged?.Invoke("Loading model…");
        string? status;
        try
        {
            bool loaded = await load();
            status = loaded ? null : "No model yet. Open Settings to download one.";
        }
        catch (Exception ex)
        {
            LogError(ex, "Loading the model failed.");
            status = "Model load failed: " + ex.Message;
        }
        // The endpoint may have been picked again while the model loaded. That switch frees it.
        ModelStatusChanged?.Invoke(Settings.Engine == EngineKind.Local ? status : null);
    }

    /// <summary>Frees the local model. The local engine suggests nothing until a model is loaded again.</summary>
    public async Task UnloadModelAsync()
    {
        await Runtime.UnloadAsync().ConfigureAwait(false);
        _ui.Post(_ => SettingsApplied?.Invoke(), null);
    }

    private void OnGlobalToggleKey(object? sender, KeyEventArgs args)
    {
        if (!args.Event.IsKeyDown || args.Event.IsRepeat || !Settings.GlobalToggle.Matches(args.Event)) return;
        // A shortcut recorder in Cotabby's own window must see the key, so it can be bound again.
        if (KeyRecorder.IsAnyRecording) return;
        args.Suppress = true;
        // Windows never sees the swallowed key, so the Alt or Win release that follows looks like
        // a lone tap and opens the menu bar or the Start menu. A key that does nothing, pressed
        // while the modifier is still down, breaks that up. It is sent from the hook callback so
        // it lands before the user can let go.
        if ((args.Event.Modifiers & (KeyModifiers.Alt | KeyModifiers.Win)) != 0) KeySender.SendMenuMask();
        _ui.Post(_ => Update(s =>
        {
            s.Enabled = !s.Enabled;
            s.Pause = null;
        }), null);
    }

    public void LogError(Exception ex, string message) => _logger.LogError(ex, "{Message}", message);

    /// <summary>
    /// Loads the selected model if it is already on disk. Never downloads. With the endpoint engine
    /// selected the local model is not needed, so nothing loads and the result is true.
    /// </summary>
    public async Task<bool> TryLoadCachedAsync()
    {
        if (Settings.Engine == EngineKind.Endpoint) return true;
        var model = ResolveActiveModel();
        if (!ModelDownloader.IsCached(model)) return false;
        return await LoadAsync(model).ConfigureAwait(false);
    }

    /// <summary>
    /// Downloads the selected model if needed, then loads it. Only the download can be cancelled.
    /// A cancelled load would leave no model in memory, because the old one is released first.
    /// Called on the UI thread.
    /// </summary>
    public async Task<bool> EnsureModelReadyAsync(IProgress<ModelDownloader.Progress>? progress, CancellationToken ct)
    {
        // The pick downloads even while power switching falls back to another model in the meantime.
        var selected = ModelCatalog.FindById(SelectedModelId) ?? ResolveActiveModel();
        if (!ModelDownloader.IsCached(selected))
        {
            // Back on the caller's UI thread, because resolving the model below may write the settings.
            await Downloader.DownloadAsync(selected, progress, ct);
        }
        // The selection may have changed during a long download.
        var model = ResolveActiveModel();
        if (!ModelDownloader.IsCached(model)) return Runtime.IsReady;
        return await LoadAsync(model).ConfigureAwait(false);
    }

    /// <inheritdoc cref="ModelSelection.SelectedModelId"/>
    public string SelectedModelId => ModelSelection.Current(Settings).SelectedModelId;

    /// <summary>The model that runs: the loaded one, or the one the next load picks.</summary>
    public string EffectiveModelId => Runtime.ActiveModel?.Id ?? ModelSelection.Current(Settings).ModelToLoad.Id;

    /// <summary>
    /// Records a model picked by hand. While power switching is on the pick goes into the current
    /// power source's slot, as upstream, because the switcher would otherwise put the old one back.
    /// </summary>
    public void SelectModel(string id) => Update(s => PickModel(s, id));

    /// <summary>Writes a model picked by hand into <paramref name="settings"/>, as <see cref="SelectModel"/> does.</summary>
    public static void PickModel(AppSettings settings, string id) =>
        ModelSelection.Pick(settings, id, PowerStatus.IsOnBattery);

    private async Task<bool> LoadAsync(CotabbyModel model)
    {
        await Runtime.LoadAsync(model, ModelDownloader.LocalPath(model), CancellationToken.None).ConfigureAwait(false);
        // The tray and any open window show which model is loaded. A status such as "No model yet",
        // set before a download from Settings or the first-run window, is stale once one is.
        _ui.Post(_ =>
        {
            if (Runtime.IsReady) ModelStatusChanged?.Invoke(null);
            SettingsApplied?.Invoke();
        }, null);
        return Runtime.IsReady;
    }

    /// <summary>The model to load. A missing or unknown active model id is replaced and saved. Runs on the UI thread.</summary>
    private CotabbyModel ResolveActiveModel()
    {
        var selection = ModelSelection.Current(Settings);
        var active = selection.ActiveModel;
        if (Settings.ActiveModelId != active.Id)
        {
            Settings.ActiveModelId = active.Id;
            ScheduleSave();
        }
        return selection.ModelToLoad;
    }

    private void OnBatterySaverChanged(bool on)
    {
        _logger.LogInformation("Battery saver turned {State}.", on ? "on" : "off");
        Coordinator.BatterySaverChanged();
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.StatusChange) return;
        _ui.Post(_ => LoadSelectedModelIfChanged(), null);
    }

    /// <summary>
    /// Loads the model the settings and the power source now call for, when one is already
    /// loaded and it is a different one. A first load is left to the caller that shows progress.
    /// </summary>
    private async void LoadSelectedModelIfChanged()
    {
        try
        {
            if (Settings.Engine != EngineKind.Local || !Runtime.IsReady) return;
            var wanted = ResolveActiveModel();
            if (wanted.Id == Runtime.ActiveModel?.Id || !ModelDownloader.IsCached(wanted)) return;
            await LoadAsync(wanted);
        }
        catch (Exception ex)
        {
            LogError(ex, "Switching models failed.");
        }
    }

    /// <summary>
    /// Shuts everything down. Each step runs on its own, so one failure, such as a settings file
    /// another process holds, never skips the steps after it. Steps that wait on in-flight work
    /// are bounded, so typing history is always written.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        var stepTimeout = TimeSpan.FromSeconds(2);

        Step("save settings", () =>
        {
            lock (_timerGate)
            {
                _disposed = true;
                _saveTimer.Dispose();
                _pauseEndTimer.Dispose();
            }
            SaveSettings();
        });
        Step("unsubscribe", () =>
        {
            _hook.KeyEvent -= OnGlobalToggleKey;
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            _batterySaver.Changed -= OnBatterySaverChanged;
        });
        Step("battery saver monitor", _batterySaver.Dispose);
        Step("emoji picker", EmojiPicker.Dispose);
        Step("macros", _macros.Dispose);
        Step("spelling", _spelling.Dispose);
        await StepAsync("coordinator", () => Coordinator.DisposeAsync().AsTask()).ConfigureAwait(false);
        Step("screen context", () => _screenContext?.Dispose());
        // Write the field being typed in now. The debounced background save may not have run yet.
        Step("typing history", TypingHistory.Flush);
        await StepAsync("model runtime", () => Runtime.DisposeAsync().AsTask()).ConfigureAwait(false);
        Step("endpoint client", EndpointClient.Dispose);
        Step("keyboard hook", _hook.Dispose);
        Step("focus tracker", _focus.Dispose);
        Step("http client", _http.Dispose);
        // Last, because it closes the log.
        Step("logging", () =>
        {
            _loggers.Dispose();
            _fileLog.Dispose();
        });

        void Step(string name, Action step)
        {
            try
            {
                step();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Shutdown step failed: {Step}.", name);
            }
        }

        async Task StepAsync(string name, Func<Task> step)
        {
            try
            {
                await step().WaitAsync(stepTimeout).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Shutdown step failed: {Step}.", name);
            }
        }
    }
}
