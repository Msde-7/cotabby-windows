using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Cotabby.App.Hosting;
using Cotabby.App.Settings;
using Cotabby.Core.Models;
using Cotabby.Inference;

namespace Cotabby.App.UI;

/// <summary>
/// First-run wizard. Mirrors the macOS port's <c>WelcomeCoordinator</c>:
/// a small sequence of steps that capture the few decisions Cotabby actually
/// needs (model choice, profile, defaults) and lets the user skip anything
/// they want to leave at default. Every change is committed only on Finish
/// so a back-out leaves the prior settings untouched.
/// </summary>
public partial class WelcomeWindow : Window
{
    private readonly AppHost _host;
    private int _stepIndex;
    private readonly StackPanel[] _steps;
    private readonly string[] _titles =
    {
        "Welcome to Cotabby",
        "Tell Cotabby about you",
        "Choose your model",
        "Defaults",
        "All set",
    };
    private readonly string[] _subtitles =
    {
        "Ghost-text autocomplete that runs entirely on this machine.",
        "Optional — Cotabby will guess if you skip this.",
        "You can change this any time from Settings.",
        "These are easy to tweak later, too.",
        "Cotabby will live in your system tray.",
    };

    private sealed record ModelRow(string Id, string DisplayName, string SizeDisplay);
    private sealed record ChoiceItem(string Id, string Label)
    {
        public override string ToString() => Label;
    }

    public WelcomeWindow(AppHost host)
    {
        _host = host;
        InitializeComponent();

        _steps = new[] { StepWelcome, StepProfile, StepModel, StepBehavior, StepReady };

        // Populate steps with whatever the user already has so re-running
        // the wizard from the tray is non-destructive.
        var s = _host.Settings;
        NameBox.Text = s.UserName;
        LanguagesBox.Text = string.Join(", ", s.Languages);

        LengthCombo.ItemsSource = CompletionLengthPreset.All
            .Select(t => new ChoiceItem(t.Id, t.Label)).ToList();
        LengthCombo.SelectedItem = ((IEnumerable<ChoiceItem>)LengthCombo.ItemsSource)
            .FirstOrDefault(c => c.Id == s.CompletionLengthPreset)
            ?? ((IEnumerable<ChoiceItem>)LengthCombo.ItemsSource).First();

        ModelChoiceList.ItemsSource = ModelCatalog.All
            .Select(m => new ModelRow(
                m.Id, m.DisplayName,
                $"{m.ApproxSizeBytes / 1024.0 / 1024.0:N0} MB" +
                    (ModelDownloader.IsCached(m) ? " · downloaded" : "")))
            .ToList();
        ModelChoiceList.SelectedItem = ((IEnumerable<ModelRow>)ModelChoiceList.ItemsSource)
            .FirstOrDefault(r => r.Id == (s.ActiveModelId ?? ModelCatalog.All[0].Id))
            ?? ((IEnumerable<ModelRow>)ModelChoiceList.ItemsSource).First();

        LaunchAtLoginCheck.IsChecked = s.LaunchAtLogin || LaunchAtLogin.IsEnabled();
        EmojiCheck.IsChecked = s.EmojiPickerEnabled;
        MultiLineCheck.IsChecked = s.AllowMultiLine;
        ShowHintCheck.IsChecked = s.ShowAcceptanceHint;

        ColorCombo.ItemsSource = GhostTextPalette.Choices;
        ColorCombo.SelectedItem = GhostTextPalette.Choices.FirstOrDefault(c => c.Id == s.GhostTextColor)
            ?? GhostTextPalette.Choices[0];

        ShowStep(0);
    }

    public static void ShowFor(AppHost host)
    {
        var w = new WelcomeWindow(host);
        w.Show();
    }

    private void ShowStep(int idx)
    {
        _stepIndex = Math.Clamp(idx, 0, _steps.Length - 1);
        for (int i = 0; i < _steps.Length; i++)
        {
            _steps[i].Visibility = i == _stepIndex ? Visibility.Visible : Visibility.Collapsed;
        }
        StepTitle.Text = _titles[_stepIndex];
        StepSubtitle.Text = _subtitles[_stepIndex];
        StepIndex.Text = $"Step {_stepIndex + 1} of {_steps.Length}";

        BackButton.IsEnabled = _stepIndex > 0;
        NextButton.Content = _stepIndex == _steps.Length - 1 ? "Finish" : "Next";
    }

    private void OnBack(object sender, RoutedEventArgs e) => ShowStep(_stepIndex - 1);

    private void OnNext(object sender, RoutedEventArgs e)
    {
        if (_stepIndex == _steps.Length - 1)
        {
            CommitAndClose();
            return;
        }
        ShowStep(_stepIndex + 1);
    }

    private void OnSkip(object sender, RoutedEventArgs e)
    {
        // Skip preserves the user's existing settings. Clear the
        // first-run flag so we don't pester them on next launch.
        _host.Settings.ShowFirstRunWelcome = false;
        _host.PersistSettings();
        Close();
    }

    private void CommitAndClose()
    {
        var s = _host.Settings;
        s.UserName = NameBox.Text?.Trim() ?? "";
        s.Languages = (LanguagesBox.Text ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => l.Length > 0).Take(6).ToList();
        if (s.Languages.Count == 0) s.Languages.Add("English");

        if (LengthCombo.SelectedItem is ChoiceItem len) s.CompletionLengthPreset = len.Id;
        if (ModelChoiceList.SelectedItem is ModelRow row) s.ActiveModelId = row.Id;

        s.LaunchAtLogin = LaunchAtLoginCheck.IsChecked == true;
        s.EmojiPickerEnabled = EmojiCheck.IsChecked == true;
        s.AllowMultiLine = MultiLineCheck.IsChecked == true;
        s.ShowAcceptanceHint = ShowHintCheck.IsChecked == true;
        if (ColorCombo.SelectedItem is GhostTextPalette.Choice cc) s.GhostTextColor = cc.Id;

        s.ShowFirstRunWelcome = false;
        _host.PersistSettings();

        try
        {
            _host.Overlay.ApplyAppearance(s.GhostTextColor, s.GhostTextOpacity, s.ShowAcceptanceHint);
        }
        catch { /* overlay not realized yet — first show will pick up */ }

        Close();
    }
}
