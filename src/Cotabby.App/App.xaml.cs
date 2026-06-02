using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Cotabby.App.Hosting;
using Cotabby.App.Overlay;
using Cotabby.App.Tray;

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
    }

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
