using System.Drawing;
using System.Drawing.Drawing2D;
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
        };
        TrySetIcon(_tray);
        // ForceCreate registers the icon with the shell. Without it, H.NotifyIcon
        // waits until the FrameworkElement is added to a visual tree, which never
        // happens for a tray-only app — so the icon never appears in the tray.
        _tray.ForceCreate();

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
        // Always generate a programmatic icon. ExtractAssociatedIcon returns the
        // generic app icon when the exe has no embedded icon, which looks like
        // every other random .NET process — easy to miss in the system tray.
        try
        {
            tray.Icon = BuildBrandIcon();
        }
        catch (Exception) { /* H.NotifyIcon falls back to its default */ }
    }

    /// <summary>
    /// Render a 32×32 dark-blue rounded square with a white "C" — works on both
    /// light and dark Windows themes and is distinguishable from the generic
    /// .NET diamond.
    /// </summary>
    private static Icon BuildBrandIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            using var bg = new SolidBrush(Color.FromArgb(255, 33, 89, 200));
            using var path = RoundedRect(new Rectangle(0, 0, 32, 32), 7);
            g.FillPath(bg, path);
            using var fg = new SolidBrush(Color.White);
            using var font = new Font("Segoe UI", 18f, System.Drawing.FontStyle.Bold, GraphicsUnit.Pixel);
            var sz = g.MeasureString("C", font);
            g.DrawString("C", font, fg, (32 - sz.Width) / 2f, (32 - sz.Height) / 2f - 1);
        }
        // Bitmap -> Icon via HICON. The caller owns disposal of the returned Icon.
        IntPtr hicon = bmp.GetHicon();
        return (Icon)Icon.FromHandle(hicon).Clone();
    }

    private static System.Drawing.Drawing2D.GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        var p = new System.Drawing.Drawing2D.GraphicsPath();
        int d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    public void Dispose()
    {
        _tray.Dispose();
    }
}
