using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Cotabby.App.Hosting;
using Cotabby.App.UI;
using Cotabby.Core.Models;
using H.NotifyIcon;

namespace Cotabby.App.Tray;

/// <summary>
/// Owns the system tray icon and its context menu. Mirrors the macOS port's
/// menu-bar controller. Knows how to: toggle the enabled state, surface the
/// resident model and let the user switch, show download progress as a
/// transient menu item, and quit the app.
/// </summary>
public sealed class TrayController : IDisposable
{
    private readonly AppHost _host;
    private readonly TaskbarIcon _tray;
    private readonly MenuItem _enabledItem;
    private readonly MenuItem _modelMenu;
    private readonly MenuItem _statusItem;

    public TrayController(AppHost host)
    {
        _host = host;
        _tray = new TaskbarIcon
        {
            ToolTipText = "Cotabby — local AI autocomplete",
            Visibility = Visibility.Visible,
        };
        TrySetIcon(_tray);

        _enabledItem = new MenuItem
        {
            Header = "Enabled",
            IsCheckable = true,
            IsChecked = host.Settings.Enabled,
        };
        _enabledItem.Click += OnToggleEnabled;

        _modelMenu = new MenuItem { Header = "Model" };
        foreach (var model in ModelCatalog.All)
        {
            var item = new MenuItem
            {
                Header = model.DisplayName,
                IsCheckable = true,
                IsChecked = model.Id == host.Settings.ActiveModelId,
                Tag = model.Id,
            };
            item.Click += (_, _) => _ = SwitchModelAsync(model.Id);
            _modelMenu.Items.Add(item);
        }

        _statusItem = new MenuItem { Header = "Loading model…", IsEnabled = false };

        var openSettingsItem = new MenuItem { Header = "Settings…" };
        openSettingsItem.Click += (_, _) => SettingsWindow.ShowOrFocus(host);

        var quitItem = new MenuItem { Header = "Quit Cotabby" };
        quitItem.Click += (_, _) => Application.Current.Shutdown();

        var menu = new ContextMenu();
        menu.Items.Add(_statusItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(_enabledItem);
        menu.Items.Add(_modelMenu);
        menu.Items.Add(openSettingsItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(quitItem);
        _tray.ContextMenu = menu;
    }

    public void SetStatus(string text) =>
        Application.Current.Dispatcher.Invoke(() => _statusItem.Header = text);

    private void OnToggleEnabled(object? sender, RoutedEventArgs e)
    {
        _host.Settings.Enabled = _enabledItem.IsChecked;
        _host.PersistSettings();
        _host.Coordinator.Enabled = _enabledItem.IsChecked;
    }

    private async Task SwitchModelAsync(string id)
    {
        _host.Settings.ActiveModelId = id;
        _host.PersistSettings();

        foreach (object item in _modelMenu.Items)
        {
            if (item is MenuItem mi && mi.Tag is string tagId)
            {
                mi.IsChecked = tagId == id;
            }
        }

        SetStatus("Downloading / loading model…");
        try
        {
            await _host.EnsureModelReadyAsync(null, CancellationToken.None);
            SetStatus($"Ready · {_host.Runtime.ActiveModel?.DisplayName ?? "?"}");
        }
        catch (Exception ex)
        {
            SetStatus($"Model load failed: {ex.Message}");
        }
    }

    private static void TrySetIcon(TaskbarIcon tray)
    {
        // Use the executable's embedded icon as the tray icon — falls back to
        // a default WPF blue square if no app icon is present.
        try
        {
            var exePath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exePath))
            {
                var icon = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
                if (icon is not null)
                {
                    tray.Icon = icon;
                }
            }
        }
        catch (Exception) { /* default icon */ }
    }

    public void Dispose()
    {
        _tray.Dispose();
    }
}
