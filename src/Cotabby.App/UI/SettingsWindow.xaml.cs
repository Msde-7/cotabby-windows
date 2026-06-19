using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Cotabby.App.Hosting;
using Cotabby.App.Settings;
using Cotabby.Core.Models;
using Cotabby.Inference;

namespace Cotabby.App.UI;

/// <summary>
/// Multi-tab settings window matching the macOS port's settings panes:
/// General, Engine &amp; model, Writing, Shortcuts, Apps, Advanced, About.
/// Settings live in <see cref="AppHost.Settings"/> and apply through
/// <see cref="AppHost.PersistSettings"/>; the model picker stays its own
/// async flow because downloads need progress feedback.
/// </summary>
public partial class SettingsWindow : Window
{
    private static SettingsWindow? _instance;
    private readonly AppHost _host;
    private CancellationTokenSource? _downloadCts;

    private sealed record ModelRow(string Id, string DisplayName, string SizeDisplay);
    private sealed record ChoiceItem(string Id, string Label)
    {
        public override string ToString() => Label;
    }

    public SettingsWindow(AppHost host)
    {
        _host = host;
        InitializeComponent();

        PopulateModelTab();
        PopulateGeneralTab();
        PopulateWritingTab();
        PopulateShortcutsTab();
        PopulateAppsTab();
        PopulateAdvancedTab();
        PopulateAboutTab();

        UpdateFooterStatus();

        Closed += (_, _) =>
        {
            _downloadCts?.Cancel();
            _instance = null;
        };
    }

    public static void ShowOrFocus(AppHost host)
    {
        if (_instance is not null)
        {
            _instance.Activate();
            return;
        }
        _instance = new SettingsWindow(host);
        _instance.Show();
    }

    // ---------- General ----------

    private void PopulateGeneralTab()
    {
        var s = _host.Settings;
        EnabledCheck.IsChecked = s.Enabled;
        LaunchAtLoginCheck.IsChecked = LaunchAtLogin.IsEnabled() || s.LaunchAtLogin;
        EmojiPickerCheck.IsChecked = s.EmojiPickerEnabled;
        MultiLineCheck.IsChecked = s.AllowMultiLine;
        AcceptPunctuationCheck.IsChecked = s.AcceptPunctuationWithWord;
        ShowHintCheck.IsChecked = s.ShowAcceptanceHint;
        FastModeCheck.IsChecked = s.FastMode;

        ColorCombo.ItemsSource = GhostTextPalette.Choices;
        ColorCombo.SelectedItem = GhostTextPalette.Choices.FirstOrDefault(c => c.Id == s.GhostTextColor)
            ?? GhostTextPalette.Choices[0];

        DisplayModeCombo.ItemsSource = new[]
        {
            new ChoiceItem("auto", "Auto (recommended)"),
            new ChoiceItem("inline", "Always inline"),
            new ChoiceItem("popup", "Always popup"),
        };
        DisplayModeCombo.SelectedItem = ((IEnumerable<ChoiceItem>)DisplayModeCombo.ItemsSource)
            .FirstOrDefault(c => c.Id == s.DisplayMode) ?? ((IEnumerable<ChoiceItem>)DisplayModeCombo.ItemsSource).First();

        OpacitySlider.Value = Math.Clamp(s.GhostTextOpacity, 0.30, 1.00);
        OpacityLabel.Text = $"{(int)Math.Round(OpacitySlider.Value * 100)}%";
        OpacitySlider.ValueChanged += (_, _) =>
            OpacityLabel.Text = $"{(int)Math.Round(OpacitySlider.Value * 100)}%";
    }

    // ---------- Model ----------

    private void PopulateModelTab()
    {
        var rows = ModelCatalog.All
            .Select(m => new ModelRow(
                m.Id,
                m.DisplayName,
                $"{m.ApproxSizeBytes / 1024.0 / 1024.0:N0} MB" +
                    (ModelDownloader.IsCached(m) ? " · downloaded" : "")))
            .ToList();
        ModelList.ItemsSource = rows;

        var activeId = _host.Settings.ActiveModelId ?? ModelCatalog.All[0].Id;
        ModelList.SelectedItem = rows.FirstOrDefault(r => r.Id == activeId) ?? rows[0];

        ModelStatusText.Text = _host.Runtime.IsReady
            ? $"Loaded: {_host.Runtime.ActiveModel?.DisplayName}"
            : "No model loaded.";
    }

    // ---------- Writing ----------

    private void PopulateWritingTab()
    {
        var s = _host.Settings;
        var items = CompletionLengthPreset.All
            .Select(t => new ChoiceItem(t.Id, t.Label))
            .ToArray();
        LengthCombo.ItemsSource = items;
        LengthCombo.SelectedItem = items.FirstOrDefault(c => c.Id == s.CompletionLengthPreset) ?? items[2];

        UserNameBox.Text = s.UserName;
        LanguagesBox.Text = string.Join(", ", s.Languages);
    }

    // ---------- Shortcuts ----------

    private void PopulateShortcutsTab()
    {
        var s = _host.Settings;
        AcceptWordKeyBox.Text = "Tab";
        AcceptSuggestionKeyBox.Text = s.AcceptSuggestionKey;
        GlobalToggleKeyBox.Text = s.GlobalToggleKey;
    }

    // ---------- Apps ----------

    private void PopulateAppsTab()
    {
        BlockedAppsList.ItemsSource = null;
        BlockedAppsList.ItemsSource = _host.Settings.BlockedApps.ToList();
    }

    private void OnAddBlockedApp(object sender, RoutedEventArgs e)
    {
        var raw = NewAppBox.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(raw)) return;
        var name = raw.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? raw[..^4] : raw;
        if (_host.Settings.BlockedApps.Any(x =>
                x.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            NewAppBox.Text = "";
            return;
        }
        _host.Settings.BlockedApps.Add(name);
        NewAppBox.Text = "";
        PopulateAppsTab();
    }

    private void OnRemoveBlockedApp(object sender, RoutedEventArgs e)
    {
        if (BlockedAppsList.SelectedItem is string s)
        {
            _host.Settings.BlockedApps.RemoveAll(x =>
                x.Equals(s, StringComparison.OrdinalIgnoreCase));
            PopulateAppsTab();
        }
    }

    // ---------- Advanced ----------

    private void PopulateAdvancedTab()
    {
        var s = _host.Settings;
        DebounceSlider.Value = Math.Clamp(s.DebounceMs, 20, 500);
        DebounceLabel.Text = $"{(int)DebounceSlider.Value} ms";
        DebounceSlider.ValueChanged += (_, _) =>
            DebounceLabel.Text = $"{(int)DebounceSlider.Value} ms";

        SettingsPathText.Text = $"Settings file: {_host.SettingsStore.Path}";
        ModelsPathText.Text = $"Models folder: {ModelCatalog.DefaultLocalDirectory()}";
        LogPathText.Text = "Log file: C:\\tmp\\cotabby-live.log";
    }

    private void OnOpenModelsFolder(object sender, RoutedEventArgs e)
    {
        var path = ModelCatalog.DefaultLocalDirectory();
        Directory.CreateDirectory(path);
        TryShellOpen(path);
    }

    private void OnOpenLog(object sender, RoutedEventArgs e) =>
        TryShellOpen(@"C:\tmp\cotabby-live.log");

    private void OnOpenSettingsFile(object sender, RoutedEventArgs e) =>
        TryShellOpen(_host.SettingsStore.Path);

    private static void TryShellOpen(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"")
            {
                UseShellExecute = true,
            });
        }
        catch { /* user can copy the path from the label */ }
    }

    // ---------- About ----------

    private void PopulateAboutTab()
    {
        var asm = typeof(SettingsWindow).Assembly.GetName();
        VersionText.Text = $"Version {asm.Version}";
    }

    // ---------- Footer ----------

    private void UpdateFooterStatus()
    {
        FooterStatus.Text = _host.Runtime.IsReady
            ? $"Model ready · {_host.Runtime.ActiveModel?.DisplayName}"
            : "No model loaded — pick one on the Engine & model tab.";
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var s = _host.Settings;
        s.Enabled = EnabledCheck.IsChecked == true;
        s.LaunchAtLogin = LaunchAtLoginCheck.IsChecked == true;
        s.EmojiPickerEnabled = EmojiPickerCheck.IsChecked == true;
        s.AllowMultiLine = MultiLineCheck.IsChecked == true;
        s.AcceptPunctuationWithWord = AcceptPunctuationCheck.IsChecked == true;
        s.ShowAcceptanceHint = ShowHintCheck.IsChecked == true;
        s.FastMode = FastModeCheck.IsChecked == true;

        if (ColorCombo.SelectedItem is GhostTextPalette.Choice color)
        {
            s.GhostTextColor = color.Id;
        }
        if (DisplayModeCombo.SelectedItem is ChoiceItem dm) s.DisplayMode = dm.Id;
        s.GhostTextOpacity = Math.Round(OpacitySlider.Value, 2);

        if (LengthCombo.SelectedItem is ChoiceItem len) s.CompletionLengthPreset = len.Id;
        s.UserName = UserNameBox.Text?.Trim() ?? "";
        s.Languages = (LanguagesBox.Text ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => l.Length > 0)
            .Take(6)
            .ToList();
        if (s.Languages.Count == 0) s.Languages.Add("English");

        s.AcceptSuggestionKey = AcceptSuggestionKeyBox.Text?.Trim() ?? "";
        s.GlobalToggleKey = GlobalToggleKeyBox.Text?.Trim() ?? "";

        s.DebounceMs = (int)Math.Round(DebounceSlider.Value);

        _host.PersistSettings();

        // Push appearance changes into the overlay so they take effect now.
        try
        {
            _host.Overlay.ApplyAppearance(s.GhostTextColor, s.GhostTextOpacity, s.ShowAcceptanceHint);
        }
        catch { /* overlay may not be realized yet — first show will pick them up */ }

        FooterStatus.Text = "Saved.";
    }

    private async void OnUseModel(object sender, RoutedEventArgs e)
    {
        if (ModelList.SelectedItem is not ModelRow row) return;
        var model = ModelCatalog.FindById(row.Id);
        if (model is null) return;

        UseButton.IsEnabled = false;
        ModelList.IsEnabled = false;
        DownloadProgress.Visibility = Visibility.Visible;
        DownloadProgress.Value = 0;
        ModelStatusText.Text = "Preparing…";

        _downloadCts = new CancellationTokenSource();
        var progress = new Progress<ModelDownloader.Progress>(p =>
        {
            if (p.TotalBytes is { } total && total > 0)
            {
                DownloadProgress.Value = (double)p.BytesDownloaded / total;
                ModelStatusText.Text = $"Downloading… {p.BytesDownloaded / 1024 / 1024:N0} / {total / 1024 / 1024:N0} MB";
            }
            else
            {
                ModelStatusText.Text = $"Downloading… {p.BytesDownloaded / 1024 / 1024:N0} MB";
            }
        });

        try
        {
            _host.Settings.ActiveModelId = model.Id;
            _host.PersistSettings();

            await _host.EnsureModelReadyAsync(progress, _downloadCts.Token);
            ModelStatusText.Text = $"Ready · {model.DisplayName}";
            UpdateFooterStatus();
            // Refresh the row label so "downloaded" badge updates.
            PopulateModelTab();
        }
        catch (OperationCanceledException)
        {
            ModelStatusText.Text = "Cancelled.";
        }
        catch (Exception ex)
        {
            ModelStatusText.Text = "Failed: " + ex.Message;
        }
        finally
        {
            DownloadProgress.Visibility = Visibility.Collapsed;
            UseButton.IsEnabled = true;
            ModelList.IsEnabled = true;
        }
    }
}
