using System.Drawing;
using System.Windows;
using System.Windows.Controls;
using Cotabby.App.Hosting;
using Cotabby.App.Settings;
using Cotabby.App.UI;
using Cotabby.Core.Models;
using Cotabby.Core.Suggestions;
using Cotabby.Inference;
using H.NotifyIcon;

namespace Cotabby.App.Tray;

/// <summary>Owns the system tray icon and its menu.</summary>
public sealed class TrayController : IDisposable
{
    private readonly AppHost _host;
    private readonly TaskbarIcon _tray;
    private readonly MenuItem _statusItem = new() { IsEnabled = false };
    private readonly MenuItem _wordCountItem = new() { IsEnabled = false };
    private readonly MenuItem _enabledItem = new() { Header = "Enabled", IsCheckable = true };
    private readonly MenuItem _pauseMenu = new() { Header = "Pause" };
    private readonly MenuItem _resumeItem = new() { Header = "Resume" };
    private readonly MenuItem _modelMenu = new() { Header = "Model" };
    private readonly MenuItem _lengthMenu = new() { Header = "Completion length" };
    private string? _statusOverride;

    public TrayController(AppHost host)
    {
        _host = host;
        _tray = new TaskbarIcon { ToolTipText = "Cotabby" };
        using (var icon = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/Cotabby.ico"))!.Stream)
        {
            _tray.Icon = new Icon(icon);
        }
        // A tray-only app never joins a visual tree, so the icon has to be created explicitly.
        _tray.ForceCreate();

        _enabledItem.Click += (_, _) => Update(s => s.Enabled = _enabledItem.IsChecked);
        _resumeItem.Click += (_, _) => Update(s => s.Pause = null);
        foreach (var duration in Enum.GetValues<PauseDuration>())
        {
            var item = new MenuItem { Header = PauseState.MenuLabel(duration) };
            item.Click += (_, _) => Update(s => s.Pause = PauseState.For(duration, DateTimeOffset.Now));
            _pauseMenu.Items.Add(item);
        }
        foreach (var preset in WordCountPreset.All)
        {
            var item = new MenuItem { Header = preset.Label, Tag = preset.Id };
            item.Click += (_, _) => Update(s =>
            {
                s.CompletionLengthPreset = preset.Id;
                s.UseCustomWordRange = false;
            });
            _lengthMenu.Items.Add(item);
        }

        var settingsItem = new MenuItem { Header = "Settings…" };
        settingsItem.Click += (_, _) => SettingsWindow.ShowOrFocus(host);
        var welcomeItem = new MenuItem { Header = "Run welcome again…" };
        welcomeItem.Click += (_, _) => WelcomeWindow.ShowFor(host);
        var quitItem = new MenuItem { Header = "Quit Cotabby" };
        quitItem.Click += (_, _) => Application.Current.Shutdown();

        var menu = new ContextMenu();
        foreach (var item in new Control[]
                 {
                     _statusItem, _wordCountItem, new Separator(),
                     _enabledItem, _pauseMenu, _resumeItem, _modelMenu, _lengthMenu, new Separator(),
                     settingsItem, welcomeItem, new Separator(), quitItem,
                 })
        {
            menu.Items.Add(item);
        }
        // Refreshing on open keeps a pause that expired in the background from showing as active.
        menu.Opened += (_, _) => Refresh();
        _tray.ContextMenu = menu;
        _tray.TrayLeftMouseUp += (_, _) => SettingsWindow.ShowOrFocus(host);

        host.SettingsApplied += Refresh;
        Refresh();
    }

    /// <summary>Shows a transient status such as a load failure. Null returns to the normal status.</summary>
    public void SetStatus(string? text) => Application.Current.Dispatcher.Invoke(() =>
    {
        _statusOverride = text;
        Refresh();
    });

    private void Update(Action<AppSettings> change)
    {
        change(_host.Settings);
        _host.PersistSettings();
    }

    private void Refresh()
    {
        var settings = _host.Settings;
        var now = DateTimeOffset.Now;
        bool paused = settings.Pause?.IsActive(now) == true;

        _statusItem.Header = _statusOverride
            ?? (!settings.Enabled ? "Cotabby is off"
                : paused ? settings.Pause!.StatusText(now)
                : settings.Engine == EngineKind.Endpoint ? $"Endpoint · {settings.EndpointModel}"
                : _host.Runtime.ActiveModel is { } active ? $"Ready · {active.DisplayName}"
                : "No model loaded");
        _tray.ToolTipText = $"Cotabby · {_statusItem.Header}";

        long words = _host.Coordinator.Metrics.WordsAccepted;
        _wordCountItem.Header = $"{words:N0} words completed";
        _wordCountItem.Visibility = settings.ShowTrayWordCount && words > 0 ? Visibility.Visible : Visibility.Collapsed;

        _enabledItem.IsChecked = settings.Enabled;
        _pauseMenu.Visibility = settings.Enabled && !paused ? Visibility.Visible : Visibility.Collapsed;
        _resumeItem.Visibility = settings.Enabled && paused ? Visibility.Visible : Visibility.Collapsed;
        // Rebuilt each time so a model dropped into the folder shows up without a restart.
        _modelMenu.Items.Clear();
        foreach (var model in ModelCatalog.Available())
        {
            var item = new MenuItem { Header = model.DisplayName, ToolTip = model.Subtitle, Tag = model.Id };
            item.Click += (_, _) => _ = SwitchModelAsync(model.Id);
            _modelMenu.Items.Add(item);
        }
        Check(_modelMenu, settings.ActiveModelId);
        Check(_lengthMenu, settings.UseCustomWordRange ? null : settings.CompletionLengthPreset);
    }

    private static void Check(MenuItem menu, string? selectedTag)
    {
        foreach (MenuItem item in menu.Items) item.IsChecked = (string)item.Tag == selectedTag;
    }

    private async Task SwitchModelAsync(string id)
    {
        Update(s => s.ActiveModelId = id);
        if (ModelCatalog.FindById(id) is { } model && !ModelDownloader.IsCached(model))
        {
            // Downloads are several GB, so they start from Settings where progress is visible.
            SettingsWindow.ShowOrFocus(_host);
            return;
        }
        SetStatus("Loading model…");
        try
        {
            await _host.EnsureModelReadyAsync(null, CancellationToken.None);
            SetStatus(null);
        }
        catch (Exception ex)
        {
            SetStatus("Model load failed: " + ex.Message);
        }
    }

    public void Dispose() => _tray.Dispose();
}
