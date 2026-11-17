using System.IO;
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
        "Local\\Cotabby.SingleInstance.{6f3b1d44-2c1b-4d0f-8c3d-2a1f1f7b1b2a}";

    /// <summary>
    /// Longest wait for the host to shut down. Exit blocks the UI thread for it, so a step stuck on
    /// in-flight work cannot keep the process alive forever.
    /// </summary>
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(8);

    private Mutex? _singleInstance;
    private AppHost? _host;
    private TrayController? _tray;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Attached first, so a failure while the host is built is logged too.
        DispatcherUnhandledException += OnUiException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        _singleInstance = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out bool createdNew);
        if (!createdNew)
        {
            _singleInstance.Dispose();
            _singleInstance = null;
            MessageBox.Show("Cotabby is already running. Check the system tray.",
                "Cotabby", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        try
        {
            // Realizing the windows now makes the first suggestion and the first popup instant.
            var overlay = Realized(new GhostOverlayWindow());
            var macroPreview = Realized(new GhostOverlayWindow());
            var emojiPopup = Realized(new EmojiPopupWindow());

            _host = new AppHost(SynchronizationContext.Current!, overlay, macroPreview, emojiPopup);
            _tray = new TrayController(_host);
            _host.Coordinator.Start();
        }
        catch (Exception ex)
        {
            // Without a host there is no tray icon to quit from, so the process must not linger.
            string? logged = Log(ex, "Cotabby failed to start.");
            MessageBox.Show(
                $"Cotabby could not start: {ex.Message}" + (logged is null ? "" : $"\n\nDetails are in {logged}"),
                "Cotabby", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

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
            bool loaded = await host.TryLoadCachedAsync();
            tray.SetStatus(loaded ? null : "No model yet. Open Settings to download one.");
        }
        catch (Exception ex)
        {
            tray.SetStatus("Model load failed: " + ex.Message);
        }
    }

    /// <summary>
    /// Shuts down synchronously. An async exit would return to WPF at its first await, and the
    /// dispatcher stops before the rest runs, so typing history, the model and the mutex were
    /// never released. The host is disposed on the thread pool, so steps that post back to the
    /// UI thread cannot deadlock against this wait.
    /// </summary>
    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _tray?.Dispose();
        }
        catch (Exception ex)
        {
            Log(ex, "Disposing the tray icon failed.");
        }

        if (_host is { } host)
        {
            try
            {
                var disposal = Task.Run(async () => await host.DisposeAsync().ConfigureAwait(false));
                if (!disposal.Wait(ShutdownTimeout))
                {
                    Log(new TimeoutException($"Shutdown did not finish within {ShutdownTimeout.TotalSeconds:0} s."),
                        "Shutdown timed out.");
                }
            }
            catch (Exception ex)
            {
                Log(ex, "Shutdown failed.");
            }
        }

        if (_singleInstance is not null)
        {
            try
            {
                _singleInstance.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // Not owned by this thread. Windows releases it when the process exits.
            }
            _singleInstance.Dispose();
            _singleInstance = null;
        }
        base.OnExit(e);
    }

    private void OnUiException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // A failed settings click must not take down the keyboard hook with it.
        e.Handled = true;
        Log(e.Exception, "Unhandled UI exception.");
    }

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e) =>
        Log(e.ExceptionObject as Exception,
            e.IsTerminating ? "Unhandled exception. Cotabby is closing." : "Unhandled exception.");

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Log(e.Exception, "Unobserved task exception.");
        e.SetObserved();
    }

    /// <summary>
    /// Logs through the host when there is one, and straight to the log file otherwise, such as
    /// when the host failed to build or has already shut its logger down. Returns the file the
    /// line went to, or null when it could not be written.
    /// </summary>
    private string? Log(Exception? ex, string message)
    {
        try
        {
            if (_host is { } host && ex is not null)
            {
                host.LogError(ex, message);
                return AppHost.LogPath;
            }
            string line = $"{DateTime.Now:HH:mm:ss.fff} fail: Cotabby.App[0] {message}{(ex is null ? "" : $" :: {ex}")}{Environment.NewLine}";
            Directory.CreateDirectory(Path.GetDirectoryName(AppHost.LogPath)!);
            try
            {
                File.AppendAllText(AppHost.LogPath, line);
                return AppHost.LogPath;
            }
            catch (IOException)
            {
                // A half-built host may still hold the log open for writing.
                string crashLog = Path.ChangeExtension(AppHost.LogPath, ".crash.log");
                File.AppendAllText(crashLog, line);
                return crashLog;
            }
        }
        catch (Exception)
        {
            // The log itself is unavailable. There is nowhere left to report to.
            return null;
        }
    }
}
