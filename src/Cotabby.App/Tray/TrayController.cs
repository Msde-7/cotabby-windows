using System.Drawing;
using System.Windows;
using System.Windows.Controls;
using Cotabby.App.Hosting;
using Cotabby.App.Settings;
using Cotabby.App.UI;
using Cotabby.App.UI.Controls;
using Cotabby.App.UI.Settings.Panes;
using Cotabby.Core.Models;
using Cotabby.Core.Suggestions;
using Cotabby.Inference;
using H.NotifyIcon;

namespace Cotabby.App.Tray;

/// <summary>
/// Owns the system tray icon and its menu. The menu mirrors upstream's menu bar panel: status,
/// pause, the context switches, engine, model and length, then links out.
/// </summary>
public sealed class TrayController : IDisposable
{
    private const string CustomLengthTag = "custom";

    private readonly AppHost _host;
    private readonly TaskbarIcon _tray;
    private readonly MenuItem _statusItem = new() { IsEnabled = false };
    private readonly MenuItem _wordCountItem = new() { IsEnabled = false };
    private readonly MenuItem _enabledItem = new() { Header = "Enabled", IsCheckable = true };
    private readonly MenuItem _pauseMenu = new() { Header = "Pause" };
    private readonly MenuItem _resumeItem = new() { Header = "Resume" };
    private readonly MenuItem _clipboardItem = new() { Header = "Include Clipboard Context", IsCheckable = true };
    private readonly MenuItem _screenItem = new() { Header = "Use Screen Context", IsCheckable = true };
    private readonly MenuItem _engineMenu = new() { Header = "Engine" };
    private readonly MenuItem _modelMenu = new() { Header = "Model" };
    private readonly MenuItem _lengthMenu = new() { Header = "Completion length" };
    private readonly MenuItem _customLengthItem = new() { Tag = CustomLengthTag };
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

        _enabledItem.Click += (_, _) => host.Update(s => s.Enabled = _enabledItem.IsChecked);
        _resumeItem.Click += (_, _) => host.Update(s => s.Pause = null);
        foreach (var duration in Enum.GetValues<PauseDuration>())
        {
            var item = new MenuItem { Header = PauseState.MenuLabel(duration) };
            item.Click += (_, _) => host.Update(s => s.Pause = PauseState.For(duration, DateTimeOffset.Now));
            _pauseMenu.Items.Add(item);
        }

        _clipboardItem.Click += (_, _) => host.Update(s => s.ClipboardContext = _clipboardItem.IsChecked);
        _screenItem.Click += (_, _) => host.Update(s => s.ScreenContext = _screenItem.IsChecked);
        if (!host.ScreenContextAvailable)
        {
            _screenItem.IsEnabled = false;
            _screenItem.Header = "Use Screen Context (unavailable)";
            _screenItem.ToolTip = "Windows has no text recognition language installed for your languages.";
            ToolTipService.SetShowOnDisabled(_screenItem, true);
        }

        foreach (var (engine, label) in new[] { (EngineKind.Local, "Local Model"), (EngineKind.Endpoint, "OpenAI-Compatible Endpoint") })
        {
            var item = new MenuItem { Header = label, Tag = engine.ToString() };
            item.Click += (_, _) => host.Update(s => s.Engine = engine);
            _engineMenu.Items.Add(item);
        }

        foreach (var preset in WordCountPreset.All)
        {
            var item = new MenuItem { Header = preset.Label, Tag = preset.Id };
            item.Click += (_, _) => host.Update(s =>
            {
                s.CompletionLengthPreset = preset.Id;
                s.UseCustomWordRange = false;
            });
            _lengthMenu.Items.Add(item);
        }
        // The custom bounds are edited in Settings. Picking this only switches back to them.
        _customLengthItem.Click += (_, _) => host.Update(s => s.UseCustomWordRange = true);
        _lengthMenu.Items.Add(_customLengthItem);

        var settingsItem = new MenuItem { Header = "Settings…" };
        settingsItem.Click += (_, _) => SettingsWindow.ShowOrFocus(host);
        var welcomeItem = new MenuItem { Header = "Run welcome again…" };
        welcomeItem.Click += (_, _) => WelcomeWindow.ShowFor(host);
        var reportItem = new MenuItem { Header = "Report a Bug…" };
        reportItem.Click += (_, _) => BugReport.Open(host);
        var versionItem = new MenuItem { Header = $"Cotabby {Branding.Version}", IsEnabled = false };
        var quitItem = new MenuItem { Header = "Quit Cotabby" };
        quitItem.Click += (_, _) => Application.Current.Shutdown();

        var menu = new ContextMenu();
        foreach (var item in new Control[]
                 {
                     _statusItem, _wordCountItem, new Separator(),
                     _enabledItem, _pauseMenu, _resumeItem, new Separator(),
                     _clipboardItem, _screenItem, new Separator(),
                     _engineMenu, _modelMenu, _lengthMenu, new Separator(),
                     settingsItem, welcomeItem, reportItem, new Separator(),
                     versionItem, quitItem,
                 })
        {
            menu.Items.Add(item);
        }
        // Refreshing on open keeps a pause that expired in the background from showing as active.
        menu.Opened += (_, _) => Refresh(menuOpen: true);
        _tray.ContextMenu = menu;
        _tray.TrayLeftMouseUp += (_, _) => SettingsWindow.ShowOrFocus(host);

        host.SettingsApplied += Refresh;
        host.ModelStatusChanged += SetStatus;
        Refresh();
    }

    /// <summary>Shows a transient status such as a load failure. Null returns to the normal status.</summary>
    private void SetStatus(string? text) => Application.Current.Dispatcher.BeginInvoke(() =>
    {
        _statusOverride = text;
        Refresh();
    });

    private void Refresh() => Refresh(_tray.ContextMenu?.IsOpen == true);

    private void Refresh(bool menuOpen)
    {
        var settings = _host.Settings;
        bool paused = settings.Pause?.IsActive(DateTimeOffset.Now) == true;
        bool local = settings.Engine == EngineKind.Local;

        _statusItem.Header = _statusOverride ?? _host.Status().Text;
        _tray.ToolTipText = $"Cotabby · {_statusItem.Header}";

        long words = _host.Coordinator.Metrics.WordsAccepted;
        _wordCountItem.Header = $"{words:N0} words completed";
        _wordCountItem.Visibility = Ui.Shown(settings.ShowTrayWordCount && words > 0);

        _enabledItem.IsChecked = settings.Enabled;
        _pauseMenu.Visibility = Ui.Shown(settings.Enabled && !paused);
        _resumeItem.Visibility = Ui.Shown(settings.Enabled && paused);
        _clipboardItem.IsChecked = settings.ClipboardContext;
        _screenItem.IsChecked = settings.ScreenContext && _host.ScreenContextAvailable;

        Check(_engineMenu, settings.Engine.ToString());
        // The local model is only loaded for the local engine, as upstream hides its Model row.
        _modelMenu.Visibility = Ui.Shown(local);
        // Listing the models reads the models folder, so a built menu waits until it is open to be seen.
        if (local && (menuOpen || _modelMenu.Items.Count == 0)) RebuildModelMenu();

        var (low, high) = WordCountPreset.Clamp(settings.CustomLowWords, settings.CustomHighWords);
        _customLengthItem.Header = $"Custom ({low}–{high} words)";
        Check(_lengthMenu, settings.UseCustomWordRange ? CustomLengthTag : settings.CompletionLengthPreset);
    }

    /// <summary>Rebuilt each time so a model dropped into the folder shows up without a restart.</summary>
    private void RebuildModelMenu()
    {
        _modelMenu.Items.Clear();
        foreach (var model in ModelCatalog.Available())
        {
            var item = new MenuItem { Header = model.DisplayName, ToolTip = model.Subtitle, Tag = model.Id };
            item.Click += (_, _) => SwitchModel(model.Id);
            _modelMenu.Items.Add(item);
        }
        // The check follows the model that actually runs, which power switching may have chosen.
        Check(_modelMenu, _host.EffectiveModelId);

        var folderItem = new MenuItem { Header = "Open Models Folder" };
        folderItem.Click += (_, _) => EnginePane.OpenModelsFolder();
        _modelMenu.Items.Add(new Separator());
        _modelMenu.Items.Add(folderItem);
    }

    private static void Check(MenuItem menu, string? selectedTag)
    {
        foreach (var item in menu.Items.OfType<MenuItem>())
        {
            if (item.Tag is string tag) item.IsChecked = tag == selectedTag;
        }
    }

    private void SwitchModel(string id)
    {
        _host.SelectModel(id);
        if (ModelCatalog.FindById(id) is { } model && !ModelDownloader.IsCached(model))
        {
            // Downloads are several GB, so they start from Settings where progress is visible.
            SettingsWindow.ShowOrFocus(_host);
            return;
        }
        _ = _host.LoadSelectedModelAsync();
    }

    public void Dispose()
    {
        _host.SettingsApplied -= Refresh;
        _host.ModelStatusChanged -= SetStatus;
        _tray.Dispose();
    }
}
