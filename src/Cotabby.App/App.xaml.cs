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
        _tray = new TrayController(_host);
        _host.Coordinator.Start();
        _tray.SetStatus("Starting…");

        _ = LoadModelAsync();

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

        // PROBE C: drive the actual coordinator end-to-end by injecting synthetic
        // key events and a synthetic focused field. This exercises the real WPF
        // overlay path, not the recording stub used in CoordinatorSmoke.
        try
        {
            Trace("PROBE C — synthetic typing through coordinator — start");
            var hook = (Cotabby.Win32.Input.KeyboardHook)
                _host!.Services.GetService(typeof(Cotabby.Core.Input.IKeyboardHook))!;
            var focus = (Cotabby.Win32.Focus.UiaFocusTracker)
                _host.Services.GetService(typeof(Cotabby.Core.Focus.IFocusTracker))!;
            var fakeField = new Cotabby.Core.Focus.FocusedField
            {
                ElementHandle = new object(),
                ProcessId = 12345,
                ProcessName = "selftest-fake",
                Text = "def fibonacci(n):\n    if n < 2:\n        return n\n    return ",
                CaretOffset = "def fibonacci(n):\n    if n < 2:\n        return n\n    return ".Length,
                CaretRect = new Cotabby.Core.Focus.ScreenRect(200, 200, 1, 20),
                FieldRect = new Cotabby.Core.Focus.ScreenRect(0, 0, 800, 600),
                IsSingleLine = false,
                IsSecure = false,
            };
            focus.SetFakeFieldForTesting(fakeField);
            // Fire ONE character down event so the coordinator triggers a request.
            hook.FireSyntheticKey(new Cotabby.Core.Input.KeyboardEvent
            {
                Kind = Cotabby.Core.Input.KeyKind.Character,
                Character = 'a',
                IsKeyDown = true,
                HasNonShiftModifier = false,
            });
            Trace("PROBE C — synthetic key fired, waiting for overlay (up to 20s)");

            // Watch for visible overlay window via WPF Visibility property.
            int waitMs = 0;
            bool overlayVisible = false;
            while (waitMs < 20000)
            {
                await Task.Delay(250);
                waitMs += 250;
                var vis = _overlay.Dispatcher.Invoke(() => _overlay.Visibility == System.Windows.Visibility.Visible
                    && !string.IsNullOrEmpty((_overlay.Content as System.Windows.Controls.Border)?.Child is System.Windows.Controls.TextBlock tb ? tb.Text : ""));
                if (vis) { overlayVisible = true; break; }
            }
            // Read the rendered text via Dispatcher hop.
            var renderedText = _overlay.Dispatcher.Invoke(() =>
            {
                if (_overlay.Content is System.Windows.Controls.Border br
                    && br.Child is System.Windows.Controls.TextBlock tb)
                    return tb.Text;
                return "<no textblock>";
            });
            if (overlayVisible) Trace($"PROBE C OK: overlay visible, text=\"{renderedText}\"");
            else Trace($"PROBE C FAIL: overlay never became visible after 20s. Last text=\"{renderedText}\"");

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
        try
        {
            var loaded = await _host.TryLoadCachedAsync(CancellationToken.None);
            if (loaded)
            {
                _tray.SetStatus($"Ready · {_host.Runtime.ActiveModel?.DisplayName}");
            }
            else
            {
                _tray.SetStatus("No model. Right-click → Settings to download one.");
            }
        }
        catch (Exception ex)
        {
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
