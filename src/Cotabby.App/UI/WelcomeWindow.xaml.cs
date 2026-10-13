using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Cotabby.App.Hosting;
using Cotabby.App.Settings;
using Cotabby.App.UI.Controls;
using Cotabby.Core.Models;
using Cotabby.Core.Suggestions;
using Cotabby.Inference;

namespace Cotabby.App.UI;

/// <summary>
/// First-run wizard with the steps welcome, starting point, personalize, keybinds and done.
/// The model download starts in the background once a starting point is confirmed, so the
/// later steps never wait on the network. The done step shows its progress.
/// </summary>
public partial class WelcomeWindow : Window
{
    private readonly AppHost _host;
    private readonly FrameworkElement[] _steps;
    private readonly (Border Card, Border Badge)[] _templates;
    private readonly List<ShortcutEditor> _shortcuts = [];
    private readonly List<Border> _capsules = [];
    private int _step;
    private string _templateModelId;

    // Background model preparation. The sequence number lets a run that was superseded,
    // because the user went back and picked another starting point, drop its late results.
    private CancellationTokenSource? _modelCts;
    private int _ensureSeq;
    private bool _ensureBusy;
    private string? _lastEnsureFor;
    private string? _modelReadyFor;
    private string? _modelError;
    private int? _modelPercent;

    public WelcomeWindow(AppHost host)
    {
        _host = host;
        InitializeComponent();
        WindowTheme.Follow(this);

        _steps = [StepWelcome, StepTemplates, StepProfile, StepKeys, StepDone];
        _templates = [(QuickCard, QuickBadge), (EverydayCard, EverydayBadge), (PowerCard, PowerBadge)];
        BuildCapsules();
        BuildShortcutRows();
        StartSpinner();

        // Seeding from the current settings makes a second run from the tray non-destructive.
        var settings = _host.Settings;
        NameBox.Text = settings.UserName;
        LanguagesHost.Content = new LanguageEditor(host);
        ModeWord.IsChecked = settings.AcceptanceMode == AcceptanceGranularity.Word;
        ModePhrase.IsChecked = settings.AcceptanceMode == AcceptanceGranularity.Phrase;
        _templateModelId = _templates.Any(template => TemplateId(template.Card) == settings.ActiveModelId)
            ? settings.ActiveModelId!
            : TemplateId(EverydayCard);
        UpdateTemplateVisuals();

        ShowStep(0);
    }

    public static void ShowFor(AppHost host) => new WelcomeWindow(host).Show();

    // ---------- Step navigation ----------

    private void BuildCapsules()
    {
        foreach (var _ in _steps)
        {
            var capsule = new Border { Height = 6, CornerRadius = new CornerRadius(3), Margin = new Thickness(3, 0, 3, 0) };
            _capsules.Add(capsule);
            CapsulePanel.Children.Add(capsule);
        }
    }

    private void ShowStep(int index)
    {
        _step = Math.Clamp(index, 0, _steps.Length - 1);
        for (int i = 0; i < _steps.Length; i++)
        {
            _steps[i].Visibility = i == _step ? Visibility.Visible : Visibility.Collapsed;
            _capsules[i].Width = i == _step ? 28 : 18;
            _capsules[i].SetResourceReference(Border.BackgroundProperty, i <= _step ? "AccentBrush" : "TrackOff");
        }

        var step = _steps[_step];
        StepCaption.Text = $"Step {_step + 1} of {_steps.Length}";
        ContinueButton.Content =
            step == StepWelcome ? "Get Started"
            : step == StepDone ? "Start Using Cotabby"
            : "Continue";
        // Hidden rather than collapsed so the footer height never jumps between steps.
        BackButton.Visibility = step == StepWelcome ? Visibility.Hidden : Visibility.Visible;
        SkipButton.Visibility = step == StepDone ? Visibility.Hidden : Visibility.Visible;

        if (step == StepTemplates) UpdateTemplateStatus();
        if (step == StepDone)
        {
            DoneKeysText.Text = DoneKeysHint();
            UpdateModelStatusUi();
        }
    }

    private void OnBack(object sender, RoutedEventArgs e) => ShowStep(_step - 1);

    private void OnContinue(object sender, RoutedEventArgs e)
    {
        if (_steps[_step] == StepTemplates) CommitTemplate();
        if (_steps[_step] == StepDone) Finish();
        else ShowStep(_step + 1);
    }

    private void OnSkip(object sender, RoutedEventArgs e) => Finish();

    /// <summary>Saves what was entered so far and stops the wizard from showing on the next launch.</summary>
    private void Finish()
    {
        var settings = _host.Settings;
        settings.UserName = NameBox.Text.Trim();
        if (settings.Languages.Count == 0) settings.Languages = [.. LanguageCatalog.DefaultLanguages];
        settings.ShowFirstRunWelcome = false;
        _host.PersistSettings();
        // A model download that is still running continues on the host after this window closes.
        Close();
    }

    // ---------- Starting point ----------

    private static string TemplateId(Border card) => (string)card.Tag;

    private void OnTemplateClicked(object sender, MouseButtonEventArgs e)
    {
        _templateModelId = TemplateId((Border)sender);
        UpdateTemplateVisuals();
        UpdateTemplateStatus();
    }

    private void UpdateTemplateVisuals()
    {
        foreach (var (card, badge) in _templates)
        {
            bool selected = TemplateId(card) == _templateModelId;
            card.SetResourceReference(Border.BorderBrushProperty, selected ? "AccentBrush" : "CardBorder");
            badge.SetResourceReference(Border.BackgroundProperty, selected ? "AccentTint" : "KeycapBg");
        }
    }

    private void UpdateTemplateStatus()
    {
        if (ModelCatalog.FindById(_templateModelId) is not { } model)
        {
            TemplateStatusText.Text = "";
            return;
        }
        bool ready = ModelDownloader.IsCached(model);
        TemplateStatusText.Text = ready
            ? "Model ready"
            : $"Will download {model.ApproxSizeBytes / 1_000_000_000.0:0.#} GB on Continue";
        TemplateStatusText.SetResourceReference(TextBlock.ForegroundProperty, ready ? "GreenBrush" : "TextSecondary");
    }

    private void CommitTemplate()
    {
        _host.Settings.ActiveModelId = _templateModelId;
        _host.PersistSettings();
        StartModelEnsure(_templateModelId);
    }

    // ---------- Background model preparation ----------

    private async void StartModelEnsure(string modelId)
    {
        if (_modelReadyFor == modelId)
        {
            UpdateModelStatusUi();
            return;
        }
        // Confirming the same starting point again must not restart a running download.
        if (_ensureBusy && _lastEnsureFor == modelId) return;

        if (_host.Runtime.IsReady && _host.Runtime.ActiveModel?.Id == modelId)
        {
            _modelReadyFor = modelId;
            _modelError = null;
            _ensureBusy = false;
            UpdateModelStatusUi();
            return;
        }

        _modelCts?.Cancel();
        _modelCts = new CancellationTokenSource();
        var seq = ++_ensureSeq;
        _lastEnsureFor = modelId;
        _ensureBusy = true;
        _modelReadyFor = null;
        _modelError = null;
        _modelPercent = null;
        UpdateModelStatusUi();

        var progress = new Progress<ModelDownloader.Progress>(p =>
        {
            if (seq != _ensureSeq) return;
            if (p.TotalBytes is long total && total > 0)
            {
                _modelPercent = (int)(p.BytesDownloaded * 100 / total);
            }
            UpdateModelStatusUi();
        });

        try
        {
            await _host.EnsureModelReadyAsync(progress, _modelCts.Token);
            if (seq == _ensureSeq)
            {
                _modelReadyFor = modelId;
                _ensureBusy = false;
            }
        }
        catch (OperationCanceledException)
        {
            if (seq == _ensureSeq) _ensureBusy = false;
        }
        catch (Exception ex)
        {
            if (seq == _ensureSeq)
            {
                _modelError = ex.Message;
                _ensureBusy = false;
            }
        }
        UpdateModelStatusUi();
    }

    private void UpdateModelStatusUi()
    {
        bool ready = _modelReadyFor is not null;
        bool busy = !ready && _modelError is null && _ensureBusy;
        DoneSpinner.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        DoneCheck.Visibility = ready ? Visibility.Visible : Visibility.Collapsed;
        DoneModelStatus.SetResourceReference(TextBlock.ForegroundProperty, ready ? "GreenBrush" : "TextSecondary");
        DoneModelStatus.Text =
            ready ? "Your model is ready"
            : _modelError is not null ? "Model setup didn't finish. You can retry from Settings."
            : !busy ? ""
            : _modelPercent is int percent ? $"Downloading your model… {percent}%"
            : "Preparing your model…";
    }

    private void StartSpinner()
    {
        // The spinner is mostly hidden. One endless animation is cheaper than
        // starting and stopping it on every status change.
        var spin = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.1)) { RepeatBehavior = RepeatBehavior.Forever };
        DoneSpinnerRotate.BeginAnimation(RotateTransform.AngleProperty, spin);
    }

    // ---------- Keybinds ----------

    private void BuildShortcutRows()
    {
        foreach (var shortcut in new[] { GlobalShortcut.AcceptWord, GlobalShortcut.AcceptAll, GlobalShortcut.Toggle })
        {
            var editor = new ShortcutEditor(shortcut, () => _host.Settings, ChangeShortcut)
            {
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
            };
            _shortcuts.Add(editor);
            ShortcutRows.Children.Add(new Grid { Children = { Ui.Text(shortcut.Name), editor } }.Styled("SettingRow"));
            ShortcutRows.Children.Add(Ui.Separator());
        }
        RefreshShortcuts();
    }

    private void ChangeShortcut(Action<AppSettings> change)
    {
        change(_host.Settings);
        _host.PersistSettings();
        RefreshShortcuts();
    }

    /// <summary>Refreshes every editor, because one binding can change how another one reads.</summary>
    private void RefreshShortcuts()
    {
        foreach (var editor in _shortcuts) editor.Refresh();
    }

    private void OnModeClicked(object sender, RoutedEventArgs e)
    {
        _host.Settings.AcceptanceMode = ModePhrase.IsChecked == true
            ? AcceptanceGranularity.Phrase
            : AcceptanceGranularity.Word;
        _host.PersistSettings();
    }

    private string DoneKeysHint()
    {
        var settings = _host.Settings;
        bool word = settings.AcceptWord.IsBound;
        bool full = GlobalShortcut.AcceptAll.Binding(settings).IsBound || (settings.DoubleTapAcceptsAll && word);
        string wordKey = GlobalShortcut.AcceptWord.Label(settings);
        string fullKey = GlobalShortcut.AcceptAll.Label(settings);
        return (word, full) switch
        {
            (true, true) => $"Start typing anywhere.\nPress {wordKey} to accept a word, {fullKey} for the full suggestion.",
            (true, false) => $"Start typing anywhere.\nPress {wordKey} to accept.",
            (false, true) => $"Start typing anywhere.\nPress {fullKey} to accept the full suggestion.",
            _ => "Start typing anywhere.",
        };
    }
}
