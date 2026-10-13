using System.Windows;
using System.Windows.Threading;
using Cotabby.App.Emoji;
using Cotabby.App.Hosting;
using Cotabby.App.Overlay;
using Cotabby.App.Tray;
using Cotabby.App.UI;

namespace Cotabby.App;

/// <summary>
/// Application entry point. The app is tray-only, so there is no startup window.
/// </summary>
public partial class App : Application
{
    private const string SingleInstanceMutexName =
        "Global\\Cotabby.SingleInstance.{6f3b1d44-2c1b-4d0f-8c3d-2a1f1f7b1b2a}";

    private Mutex? _singleInstance;
    private AppHost? _host;
    private TrayController? _tray;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstance = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out bool createdNew);
        if (!createdNew)
        {
            _singleInstance = null;
            MessageBox.Show("Cotabby is already running. Check the system tray.",
                "Cotabby", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        // Realizing the windows now makes the first suggestion and the first popup instant.
        var overlay = Realized(new GhostOverlayWindow());
        var macroPreview = Realized(new GhostOverlayWindow());
        var emojiPopup = Realized(new EmojiPopupWindow());

        _host = new AppHost(SynchronizationContext.Current!, overlay, macroPreview, emojiPopup);
        DispatcherUnhandledException += OnUiException;

        _tray = new TrayController(_host);
        _host.Coordinator.Start();
        _ = LoadModelAsync(_host, _tray);

        if (_host.Settings.ShowFirstRunWelcome)
        {
            Dispatcher.BeginInvoke(() => WelcomeWindow.ShowFor(_host), DispatcherPriority.Background);
        }
    }

    private static T Realized<T>(T window) where T : Window
    {
        window.Show();
        window.Hide();
        return window;
    }

    private static async Task LoadModelAsync(AppHost host, TrayController tray)
    {
        tray.SetStatus("Loading model…");
        try
        {
            bool loaded = await host.TryLoadCachedAsync(CancellationToken.None);
            tray.SetStatus(loaded ? null : "No model yet. Open Settings to download one.");
        }
        catch (Exception ex)
        {
            tray.SetStatus("Model load failed: " + ex.Message);
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
            _singleInstance?.ReleaseMutex();
            base.OnExit(e);
        }
    }

    private void OnUiException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // A failed settings click must not take down the keyboard hook with it.
        e.Handled = true;
        _host?.LogError(e.Exception, "Unhandled UI exception.");
    }
}
