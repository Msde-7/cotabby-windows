using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Cotabby.App.Hosting;
using Cotabby.App.Settings;
using Cotabby.App.UI;
using Cotabby.Core.Focus;
using Cotabby.Core.Models;
using Cotabby.Core.Suggestions;
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
    private readonly MenuItem _lengthMenu;

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

        // Completion-length submenu — mirrors the macOS "completion length"
        // quick-pick in the menu bar. Lets the user widen/shrink the budget
        // without opening the full Settings window.
        var lengthMenu = new MenuItem { Header = "Completion length" };
        foreach (var (id, label) in CompletionLengthPreset.All)
        {
            var item = new MenuItem
            {
                Header = label,
                IsCheckable = true,
                IsChecked = id == host.Settings.CompletionLengthPreset,
                Tag = id,
            };
            item.Click += (_, _) => SetLengthPreset(id);
            lengthMenu.Items.Add(item);
        }
        _lengthMenu = lengthMenu;

        var openSettingsItem = new MenuItem { Header = "Settings…" };
        openSettingsItem.Click += (_, _) => SettingsWindow.ShowOrFocus(host);

        var welcomeItem = new MenuItem { Header = "Run welcome again…" };
        welcomeItem.Click += (_, _) => WelcomeWindow.ShowFor(host);

        // Diagnostic submenu — used to isolate which subsystem is broken when
        // the live typing flow fails to surface a suggestion. Each item
        // exercises one piece in isolation:
        //   - Overlay rendering only (no inference, no UIA)
        //   - Inference only (no overlay, prints to a message box)
        //   - Inference + overlay (synthetic anchor, fixed prompt)
        var diagMenu = new MenuItem { Header = "Diagnostics" };
        var testOverlay = new MenuItem { Header = "Show test overlay (5s)" };
        testOverlay.Click += async (_, _) => await ShowTestOverlay();
        var testGenerate = new MenuItem { Header = "Run test generation" };
        testGenerate.Click += async (_, _) => await RunTestGeneration();
        var testFull = new MenuItem { Header = "Full pipeline: generate + show overlay" };
        testFull.Click += async (_, _) => await RunFullPipelineTest();
        diagMenu.Items.Add(testOverlay);
        diagMenu.Items.Add(testGenerate);
        diagMenu.Items.Add(testFull);

        var quitItem = new MenuItem { Header = "Quit Cotabby" };
        quitItem.Click += (_, _) => Application.Current.Shutdown();

        var menu = new ContextMenu();
        menu.Items.Add(_statusItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(_enabledItem);
        menu.Items.Add(_modelMenu);
        menu.Items.Add(_lengthMenu);
        menu.Items.Add(openSettingsItem);
        menu.Items.Add(welcomeItem);
        menu.Items.Add(diagMenu);
        menu.Items.Add(new Separator());
        menu.Items.Add(quitItem);
        _tray.ContextMenu = menu;
    }

    private void SetLengthPreset(string id)
    {
        _host.Settings.CompletionLengthPreset = id;
        _host.PersistSettings();
        foreach (object item in _lengthMenu.Items)
        {
            if (item is MenuItem mi && mi.Tag is string tagId)
            {
                mi.IsChecked = tagId == id;
            }
        }
    }

    private async Task ShowTestOverlay()
    {
        var overlay = (Cotabby.App.Overlay.GhostOverlayWindow)_host.Overlay;
        // Place at logical (100,100) of primary monitor. Use Win32 work-area
        // so it lands in physical-pixel space the way real usage does.
        var anchor = new ScreenRect(100, 100, 1, 24);
        SetStatus("Test overlay should appear at (100,100) for 5s.");
        overlay.Show("=== COTABBY TEST OVERLAY — if you see this, rendering works ===", anchor);
        await Task.Delay(5000);
        overlay.Hide();
        SetStatus("Test overlay done.");
    }

    private async Task RunTestGeneration()
    {
        if (!_host.Runtime.IsReady)
        {
            MessageBox.Show("Model not loaded yet. Wait for the status to say Ready first.",
                "Cotabby diagnostic", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        SetStatus("Generating test completion…");
        var engine = (ISuggestionEngine)_host.Services.GetService(typeof(ISuggestionEngine))!;
        var request = new SuggestionRequest
        {
            RequestId = "diag",
            Prefix = "def fibonacci(n):\n    if n < 2:\n        return n\n    return ",
            Suffix = string.Empty,
            HostApp = "diag",
            SingleLine = false,
            MaxTokens = 64,
        };
        var sb = new System.Text.StringBuilder();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await foreach (var chunk in engine.GenerateAsync(request, CancellationToken.None))
        {
            if (chunk.IsFinal) break;
            sb.Append(chunk.Text);
        }
        sw.Stop();
        SetStatus($"Test generation done in {sw.ElapsedMilliseconds} ms.");
        MessageBox.Show($"Engine returned in {sw.ElapsedMilliseconds} ms:\n\n{sb}",
            "Cotabby diagnostic", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async Task RunFullPipelineTest()
    {
        if (!_host.Runtime.IsReady)
        {
            MessageBox.Show("Model not loaded yet. Wait for the status to say Ready first.",
                "Cotabby diagnostic", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        SetStatus("Full pipeline test in progress…");
        var engine = (ISuggestionEngine)_host.Services.GetService(typeof(ISuggestionEngine))!;
        var overlay = (Cotabby.App.Overlay.GhostOverlayWindow)_host.Overlay;
        var request = new SuggestionRequest
        {
            RequestId = "diag-full",
            Prefix = "def fibonacci(n):\n    if n < 2:\n        return n\n    return ",
            Suffix = string.Empty,
            HostApp = "diag",
            SingleLine = false,
            MaxTokens = 32,
        };
        var anchor = new ScreenRect(100, 100, 1, 24);
        string accumulated = string.Empty;
        bool shown = false;
        await foreach (var chunk in engine.GenerateAsync(request, CancellationToken.None))
        {
            if (chunk.IsFinal) break;
            accumulated += chunk.Text;
            if (string.IsNullOrEmpty(accumulated)) continue;
            if (!shown) { overlay.Show(accumulated, anchor); shown = true; }
            else { overlay.Update(accumulated); }
        }
        await Task.Delay(5000);
        overlay.Hide();
        SetStatus("Full pipeline test done.");
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
