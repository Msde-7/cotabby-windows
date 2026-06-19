using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Cotabby.App.Hosting;
using Cotabby.App.Overlay;
using Cotabby.App.Tray;
using Microsoft.Extensions.Logging;

namespace Cotabby.App;

/// <summary>
/// Application entry point. Mirrors macOS <c>CotabbyApp</c> + <c>AppDelegate</c>:
/// constructs the long-lived overlay window and <see cref="AppHost"/>, then
/// installs the tray icon and kicks off model load. No <c>StartupUri</c> — the
/// app is intentionally headless (tray-only) and lives off the dispatcher.
/// </summary>
public partial class App : Application
{
    private AppHost? _host;
    private TrayController? _tray;
    private GhostOverlayWindow? _overlay;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnUiException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainException;
        TaskScheduler.UnobservedTaskException += OnTaskException;

        // Single-instance guard: a second launch should bring the existing
        // tray icon into focus rather than spawn a duplicate coordinator that
        // races on the keyboard hook.
        var mutex = new Mutex(initiallyOwned: true,
            name: "Global\\Cotabby.SingleInstance.{6f3b1d44-2c1b-4d0f-8c3d-2a1f1f7b1b2a}",
            createdNew: out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show("Cotabby is already running. Check the system tray.",
                "Cotabby", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }
        // Keep the mutex alive for the process lifetime; GC.KeepAlive in Exit.
        Exit += (_, _) => mutex.ReleaseMutex();

        _overlay = new GhostOverlayWindow();
        // Pre-realize the HWND so SetWindowPos works on first Show.
        _overlay.Show();
        _overlay.Hide();

        var ui = SynchronizationContext.Current
                 ?? throw new InvalidOperationException("WPF startup must have a SynchronizationContext.");
        _host = new AppHost(ui, _overlay);
        // Apply user appearance preferences before the first show so a stored
        // color/opacity choice takes effect on the very first suggestion.
        _overlay.ApplyAppearance(
            _host.Settings.GhostTextColor,
            _host.Settings.GhostTextOpacity,
            _host.Settings.ShowAcceptanceHint);
        _tray = new TrayController(_host);
        _host.Coordinator.Start();
        _tray.SetStatus("Starting…");

        _ = LoadModelAsync();

        // First-run onboarding wizard. The flag is cleared inside the window
        // when the user finishes/skips it. Defer until after the tray is up so
        // the experience starts with the tray icon already showing.
        if (_host.Settings.ShowFirstRunWelcome)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try { UI.WelcomeWindow.ShowFor(_host); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine("WelcomeWindow failed: " + ex); }
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        if (Environment.GetEnvironmentVariable("COTABBY_SELF_TEST") == "1")
        {
            _ = RunSelfTestAsync();
        }
    }

    /// <summary>
    /// Headless self-test: forces the overlay visible at a known anchor so the
    /// AppSmoke harness can EnumWindows-verify it without needing a keyboard.
    /// Kept on the same dispatcher so it interleaves correctly with the rest
    /// of the app's lifecycle.
    /// </summary>
    private async Task RunSelfTestAsync()
    {
        void Trace(string s)
        {
            try { File.AppendAllText(@"C:\tmp\cotabby-selftest.txt", DateTime.Now.ToString("HH:mm:ss.fff ") + s + Environment.NewLine); } catch { }
        }
        try { File.WriteAllText(@"C:\tmp\cotabby-selftest.txt", ""); } catch { }
        Trace("RunSelfTestAsync start");

        await Task.Delay(2000);
        Trace("after initial 2s delay");
        if (_overlay is null) { Trace("no overlay; abort"); return; }
        Trace("about to call overlay.Show");

        var anchor = new Cotabby.Core.Focus.ScreenRect(100, 100, 1, 24);
        try { _overlay.Show("=== SELF-TEST: Cotabby overlay is rendering ===", anchor); Trace("overlay.Show returned"); }
        catch (Exception ex) { Trace("overlay.Show THREW: " + ex); }

        try { _tray?.SetStatus("Self-test overlay shown."); Trace("SetStatus returned"); }
        catch (Exception ex) { Trace("SetStatus THREW: " + ex); }

        Trace("entering runtime-wait loop");
        // Wait until runtime is ready, with a 60s ceiling so the trace tells us
        // if the model load is blocking instead of just sitting at the 15s mark.
        for (int i = 0; i < 60; i++)
        {
            if (_host?.Runtime.IsReady == true) { Trace("runtime became ready"); break; }
            await Task.Delay(1000);
            Trace($"waiting for runtime (t={i+1}s, ready={_host?.Runtime.IsReady})");
        }

        if (_host?.Runtime.IsReady != true)
        {
            Trace("runtime never ready; aborting engine probe");
            return;
        }
        Trace("runtime ready, starting engine call");

        // Probe A: shared, DI-managed engine (what the coordinator uses).
        try
        {
            var engine = (Cotabby.Core.Suggestions.ISuggestionEngine)
                _host.Services.GetService(typeof(Cotabby.Core.Suggestions.ISuggestionEngine))!;
            var req = MakeProbeRequest();
            Trace("PROBE A — DI engine — entering await foreach");
            string accumulated = string.Empty;
            int chunkCount = 0;
            await foreach (var chunk in engine.GenerateAsync(req, CancellationToken.None))
            {
                chunkCount++;
                if (chunk.IsFinal) break;
                accumulated += chunk.Text;
            }
            Trace($"PROBE A OK: chunks={chunkCount} accumulated=\"{accumulated}\"");
        }
        catch (Exception ex)
        {
            Trace("PROBE A FAILED: " + ex.GetType().Name + ": " + ex.Message);
        }

        // Probe B: fresh runtime + fresh executor on the same model file.
        try
        {
            Trace("PROBE B — fresh runtime — starting load");
            using var lf = Microsoft.Extensions.Logging.LoggerFactory.Create(b => b.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Warning));
            await using var freshRuntime = new Cotabby.Inference.LlamaRuntimeManager(
                lf.CreateLogger<Cotabby.Inference.LlamaRuntimeManager>());
            var model = Cotabby.Core.Models.ModelCatalog.All[0];
            var path = Cotabby.Inference.ModelDownloader.LocalPath(model);
            await freshRuntime.LoadAsync(model, path, CancellationToken.None);
            var freshEngine = new Cotabby.Inference.LlamaSuggestionEngine(
                freshRuntime, lf.CreateLogger<Cotabby.Inference.LlamaSuggestionEngine>());
            var req = MakeProbeRequest();
            Trace("PROBE B — fresh engine — entering await foreach");
            string accumulated = string.Empty;
            int chunkCount = 0;
            await foreach (var chunk in freshEngine.GenerateAsync(req, CancellationToken.None))
            {
                chunkCount++;
                if (chunk.IsFinal) break;
                accumulated += chunk.Text;
            }
            Trace($"PROBE B OK: chunks={chunkCount} accumulated=\"{accumulated}\"");
        }
        catch (Exception ex)
        {
            Trace("PROBE B FAILED: " + ex.GetType().Name + ": " + ex.Message);
        }

        // Hide the initial test overlay so PROBE C starts from a clean state.
        _overlay.Hide();
        await Task.Delay(500);

        try
        {
            Trace("PROBE C — synthetic typing through coordinator — start");
            var hook = (Cotabby.Win32.Input.KeyboardHook)
                _host!.Services.GetService(typeof(Cotabby.Core.Input.IKeyboardHook))!;
            var focus = (Cotabby.Win32.Focus.UiaFocusTracker)
                _host.Services.GetService(typeof(Cotabby.Core.Focus.IFocusTracker))!;
            // Anchor at (500, 400) so we can verify the window actually moved
            // from the initial test position (40, 40).
            var probeAnchor = new Cotabby.Core.Focus.ScreenRect(500, 400, 1, 20);
            var fakeField = new Cotabby.Core.Focus.FocusedField
            {
                ElementHandle = new object(),
                ProcessId = 12345,
                ProcessName = "selftest-fake",
                Text = "def fibonacci(n):\n    if n < 2:\n        return n\n    return ",
                CaretOffset = "def fibonacci(n):\n    if n < 2:\n        return n\n    return ".Length,
                CaretRect = probeAnchor,
                FieldRect = new Cotabby.Core.Focus.ScreenRect(0, 0, 1000, 800),
                IsSingleLine = false,
                IsSecure = false,
            };
            focus.SetFakeFieldForTesting(fakeField);
            hook.FireSyntheticKey(new Cotabby.Core.Input.KeyboardEvent
            {
                Kind = Cotabby.Core.Input.KeyKind.Character,
                Character = 'a',
                IsKeyDown = true,
                HasNonShiftModifier = false,
            });
            Trace("PROBE C — synthetic key fired, polling overlay state…");

            int waitMs = 0;
            string lastText = "<none>";
            bool textChanged = false;
            int hwndX = -1, hwndY = -1, hwndW = -1, hwndH = -1;
            while (waitMs < 30000)
            {
                await Task.Delay(250);
                waitMs += 250;
                var (visible, text, rect) = _overlay.Dispatcher.Invoke(() =>
                {
                    bool vis = _overlay.Visibility == System.Windows.Visibility.Visible;
                    string t = _overlay.CurrentGhostText;
                    var src = System.Windows.Interop.HwndSource.FromVisual(_overlay) as System.Windows.Interop.HwndSource;
                    int x = 0, y = 0, w = 0, h = 0;
                    if (src is not null)
                    {
                        Cotabby.App.Overlay.GhostOverlayWindow.GetWindowRectForTest(src.Handle, out x, out y, out w, out h);
                    }
                    return (vis, t, (x, y, w, h));
                });
                lastText = text;
                (hwndX, hwndY, hwndW, hwndH) = rect;
                // A successful coordinator-driven show should change the text away
                // from anything containing "SELF-TEST" and place the window
                // somewhere near our probeAnchor (500, 400).
                if (visible && !text.Contains("SELF-TEST") && text.Length > 0)
                {
                    textChanged = true;
                    break;
                }
            }
            if (textChanged)
            {
                Trace($"PROBE C OK: overlay text=\"{lastText}\" hwnd_rect=({hwndX},{hwndY},{hwndW}x{hwndH})");
                bool nearAnchor = Math.Abs(hwndX - 500) < 400 && Math.Abs(hwndY - 400) < 400;
                Trace($"PROBE C anchor sanity: near (500,400)? {nearAnchor}");
            }
            else
            {
                Trace($"PROBE C FAIL: overlay text never changed away from SELF-TEST. Last text=\"{lastText}\" rect=({hwndX},{hwndY},{hwndW}x{hwndH})");
            }
            focus.SetFakeFieldForTesting(null);
        }
        catch (Exception ex)
        {
            Trace("PROBE C THREW: " + ex);
        }

        Trace("RunSelfTestAsync done");
    }

    private static Cotabby.Core.Suggestions.SuggestionRequest MakeProbeRequest() => new()
    {
        RequestId = "selftest",
        Prefix = "def fibonacci(n):\n    if n < 2:\n        return n\n    return ",
        Suffix = string.Empty,
        HostApp = "selftest",
        SingleLine = false,
        MaxTokens = 32,
    };

    private async Task LoadModelAsync()
    {
        if (_host is null || _tray is null) return;
        var logger = _host.Services.GetService(typeof(Microsoft.Extensions.Logging.ILoggerFactory))
            is Microsoft.Extensions.Logging.ILoggerFactory lf
            ? lf.CreateLogger("Cotabby.App.LoadModel") : null;
        try
        {
            logger?.LogInformation("LoadModelAsync starting.");
            var loaded = await _host.TryLoadCachedAsync(CancellationToken.None);
            if (loaded)
            {
                logger?.LogInformation("LoadModelAsync OK: model is ready.");
                _tray.SetStatus($"Ready · {_host.Runtime.ActiveModel?.DisplayName}");
            }
            else
            {
                logger?.LogWarning("LoadModelAsync: TryLoadCachedAsync returned false (model not cached or runtime not ready).");
                _tray.SetStatus("No model. Right-click → Settings to download one.");
            }
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "LoadModelAsync THREW.");
            _tray.SetStatus("Model load failed: " + ex.Message);
        }
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        try
        {
            _tray?.Dispose();
            if (_host is not null) await _host.DisposeAsync();
        }
        finally
        {
            base.OnExit(e);
        }
    }

    private void OnUiException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        System.Diagnostics.Debug.WriteLine($"UI exception: {e.Exception}");
    }

    private static void OnDomainException(object sender, UnhandledExceptionEventArgs e)
    {
        System.Diagnostics.Debug.WriteLine($"Domain exception: {e.ExceptionObject}");
    }

    private static void OnTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        e.SetObserved();
        System.Diagnostics.Debug.WriteLine($"Task exception: {e.Exception}");
    }
}
