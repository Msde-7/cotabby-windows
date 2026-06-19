using System.Net.Http;
using System.Threading;
using Cotabby.App.Overlay;
using Cotabby.App.Settings;
using Cotabby.Core.Focus;
using Cotabby.Core.Input;
using Cotabby.Core.Insertion;
using Cotabby.Core.Models;
using Cotabby.Core.Overlay;
using Cotabby.Core.Suggestions;
using Cotabby.Inference;
using Cotabby.Win32.Focus;
using Cotabby.Win32.Input;
using Cotabby.Win32.Insertion;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Cotabby.App.Hosting;

/// <summary>
/// Composition root. Mirrors the macOS port's <c>CotabbyAppEnvironment</c>:
/// constructs the long-lived object graph once at startup so SwiftUI / WPF
/// views never instantiate services themselves.
/// </summary>
public sealed class AppHost : IAsyncDisposable
{
    public IServiceProvider Services { get; }
    public SuggestionCoordinator Coordinator { get; }
    public LlamaRuntimeManager Runtime { get; }
    public ModelDownloader Downloader { get; }
    public SettingsStore SettingsStore { get; }
    public AppSettings Settings { get; }
    public GhostOverlayWindow Overlay { get; }

    /// <summary>
    /// Optional emoji picker controller — registered from <c>App.OnStartup</c>
    /// after the popup window is realized. Kept on the host so the settings
    /// UI can toggle <see cref="EmojiPickerController.Enabled"/>.
    /// </summary>
    public Cotabby.App.Emoji.EmojiPickerController? EmojiPicker { get; private set; }

    public void RegisterEmojiPicker(Cotabby.App.Emoji.EmojiPickerController picker)
    {
        EmojiPicker = picker;
    }

    public AppHost(SynchronizationContext uiContext, GhostOverlayWindow overlay)
    {
        var sc = new ServiceCollection();

        sc.AddLogging(b =>
        {
            b.SetMinimumLevel(LogLevel.Debug);
            b.AddDebug();
            b.AddSimpleConsole(opts =>
            {
                opts.SingleLine = true;
                opts.TimestampFormat = "HH:mm:ss.fff ";
            });
            // File sink so detailed monitoring lands somewhere stable
            // regardless of how the user launched the exe. Path is fixed at
            // C:\tmp\cotabby-live.log to match the existing tail tooling.
            b.AddProvider(new FileLoggerProvider(@"C:\tmp\cotabby-live.log"));
        });

        sc.AddSingleton<HttpClient>(_ =>
        {
            var h = new HttpClient
            {
                Timeout = TimeSpan.FromMinutes(30), // GGUF downloads are big
            };
            h.DefaultRequestHeaders.UserAgent.ParseAdd("Cotabby-Windows/0.1 (+https://github.com/Msde-7/cotabby-windows)");
            return h;
        });

        sc.AddSingleton<SettingsStore>();
        sc.AddSingleton(provider => provider.GetRequiredService<SettingsStore>().Load());

        sc.AddSingleton<IFocusTracker, UiaFocusTracker>();
        sc.AddSingleton<IKeyboardHook, KeyboardHook>();
        sc.AddSingleton<ITextInserter, SendInputTextInserter>();

        sc.AddSingleton<LlamaRuntimeManager>();
        sc.AddSingleton<ModelDownloader>();
        sc.AddSingleton<ISuggestionEngine, LlamaSuggestionEngine>();

        sc.AddSingleton<IOverlayPresenter>(_ => overlay);
        sc.AddSingleton(uiContext);

        sc.AddSingleton(provider =>
        {
            var settings = provider.GetRequiredService<AppSettings>();
            return new SuggestionWorkController(TimeSpan.FromMilliseconds(settings.DebounceMs));
        });
        sc.AddSingleton<SuggestionCoordinator>();

        Services = sc.BuildServiceProvider();
        Coordinator = Services.GetRequiredService<SuggestionCoordinator>();
        Runtime = Services.GetRequiredService<LlamaRuntimeManager>();
        Downloader = Services.GetRequiredService<ModelDownloader>();
        SettingsStore = Services.GetRequiredService<SettingsStore>();
        Settings = Services.GetRequiredService<AppSettings>();
        Overlay = overlay;

        ApplyRuntimeSettings();
    }

    /// <summary>
    /// Push the user-visible settings into the long-lived runtime (coordinator,
    /// launch-at-login). Idempotent — call after editing <see cref="Settings"/>
    /// (and before <see cref="PersistSettings"/>) to make the change take effect
    /// without restarting the app.
    /// </summary>
    public void ApplyRuntimeSettings()
    {
        Coordinator.Enabled = Settings.Enabled;

        var (single, multi) = CompletionLengthPreset.Tokens(Settings.CompletionLengthPreset);
        Coordinator.MaxTokensSingleLine = single;
        Coordinator.MaxTokensMultiLine = multi;
        Coordinator.AllowMultiLineSuggestions = Settings.AllowMultiLine;

        if (EmojiPicker is not null)
        {
            EmojiPicker.Enabled = Settings.EmojiPickerEnabled;
        }

        if (Settings.BlockedApps is { Count: > 0 } list)
        {
            Coordinator.BlockedApps = new HashSet<string>(list, StringComparer.OrdinalIgnoreCase);
        }
        else
        {
            Coordinator.BlockedApps = null;
        }

        // Best-effort. The registry write fails silently in restricted
        // environments — settings UI shows the actual queried state on next
        // open so we don't mislead the user.
        LaunchAtLogin.Set(Settings.LaunchAtLogin);
    }

    /// <summary>
    /// Try to load the resident model if already cached, but never auto-download.
    /// The user explicitly triggers a download through the settings window so we
    /// don't burn ~400MB of their bandwidth on first launch. If the configured
    /// model isn't cached we leave the runtime cold and return false.
    /// </summary>
    public async Task<bool> TryLoadCachedAsync(CancellationToken ct)
    {
        var model = ResolveActiveModel();
        if (!ModelDownloader.IsCached(model)) return false;
        var path = ModelDownloader.LocalPath(model);
        await Runtime.LoadAsync(model, path, ct).ConfigureAwait(false);
        return Runtime.IsReady;
    }

    /// <summary>
    /// Download (if needed) and load the configured model. Driven from the
    /// settings window where the user is paying attention to the progress bar.
    /// </summary>
    public async Task<bool> EnsureModelReadyAsync(IProgress<ModelDownloader.Progress>? progress, CancellationToken ct)
    {
        var model = ResolveActiveModel();
        if (!ModelDownloader.IsCached(model))
        {
            await Downloader.DownloadAsync(model, progress, ct).ConfigureAwait(false);
        }
        var path = ModelDownloader.LocalPath(model);
        await Runtime.LoadAsync(model, path, ct).ConfigureAwait(false);
        return Runtime.IsReady;
    }

    private CotabbyModel ResolveActiveModel()
    {
        var settings = Settings;
        var model = (settings.ActiveModelId is { } id ? ModelCatalog.FindById(id) : null)
                    ?? ModelCatalog.All[0];
        if (settings.ActiveModelId != model.Id)
        {
            settings.ActiveModelId = model.Id;
            SettingsStore.Save(settings);
        }
        return model;
    }

    public void PersistSettings()
    {
        ApplyRuntimeSettings();
        SettingsStore.Save(Settings);
    }

    public async ValueTask DisposeAsync()
    {
        EmojiPicker?.Dispose();
        await Coordinator.DisposeAsync().ConfigureAwait(false);
        await Runtime.DisposeAsync().ConfigureAwait(false);
    }
}
