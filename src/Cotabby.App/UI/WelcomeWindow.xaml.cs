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
    private static WelcomeWindow? _instance;

    private readonly AppHost _host;
    private readonly FrameworkElement[] _steps;
    private readonly (Border Card, Border Badge, Border Recommended)[] _templates;
    private readonly Dictionary<OnboardingTemplate, OnboardingTemplateAvailability> _availability;
    private readonly List<ShortcutEditor> _shortcuts = [];
    private readonly List<Border> _capsules = [];
    private int _step;
    private OnboardingTemplate _template;

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
        _templates =
        [
            (QuickCard, QuickBadge, QuickRecommended),
            (EverydayCard, EverydayBadge, EverydayRecommended),
            (PowerCard, PowerBadge, PowerRecommended),
        ];
        ulong memory = InstalledMemory.Bytes();
        _availability = Enum.GetValues<OnboardingTemplate>()
            .ToDictionary(template => template, template => OnboardingTemplateRecommender.Availability(template, memory));
        BuildCapsules();
        BuildShortcutRows();
        StartSpinner();

        // Seeding from the current settings makes a second run from the tray non-destructive.
        var settings = _host.Settings;
        NameBox.Text = settings.UserName;
        LanguagesHost.Content = new LanguageEditor(host);
        ModeWord.IsChecked = settings.AcceptanceMode == AcceptanceGranularity.Word;
        ModePhrase.IsChecked = settings.AcceptanceMode == AcceptanceGranularity.Phrase;
        // A second run starts on the tier whose model is already chosen, a first run on the recommended one.
        var selectedModel = host.SelectedModelId;
        _template = _availability.Values
            .FirstOrDefault(a => !a.IsDisabled && OnboardingTemplateRecommender.Plan(a.Template).Model.Id == selectedModel)
            ?.Template
            ?? OnboardingTemplateRecommender.Recommended(memory);
        UpdateTemplateVisuals();

        ShowStep(0);
    }

    /// <summary>
    /// Shows the wizard, or brings back the one already open. A second one would fail to download
    /// the model the first is fetching.
    /// </summary>
    public static void ShowFor(AppHost host)
    {
        if (_instance is null)
        {
            _instance = new WelcomeWindow(host);
            _instance.Closed += (_, _) => _instance = null;
            _instance.Show();
        }
        if (_instance.WindowState == WindowState.Minimized) _instance.WindowState = WindowState.Normal;
        _instance.Activate();
    }

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
            _steps[i].Visibility = Ui.Shown(i == _step);
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
        _host.Update(s =>
        {
            s.UserName = NameBox.Text.Trim();
            if (s.Languages.Count == 0) s.Languages = [.. LanguageCatalog.DefaultLanguages];
            s.ShowFirstRunWelcome = false;
        });
        // A model download that is still running continues on the host after this window closes,
        // and the model list in Settings can pause or cancel it.
        Close();
    }

    private static OnboardingTemplate TemplateOf(Border card) => Enum.Parse<OnboardingTemplate>((string)card.Tag);

    private void OnTemplateClicked(object sender, MouseButtonEventArgs e)
    {
        var template = TemplateOf((Border)sender);
        // A starting point this PC cannot run stays visible, with its reason, but cannot be picked.
        if (_availability[template].IsDisabled) return;
        _template = template;
        UpdateTemplateVisuals();
        UpdateTemplateStatus();
    }

    private void UpdateTemplateVisuals()
    {
        foreach (var (card, badge, recommended) in _templates)
        {
            var availability = _availability[TemplateOf(card)];
            bool selected = availability.Template == _template;
            card.SetResourceReference(Border.BorderBrushProperty, selected ? "AccentBrush" : "CardBorder");
            badge.SetResourceReference(Border.BackgroundProperty, selected ? "AccentTint" : "KeycapBg");
            recommended.Visibility = Ui.Shown(availability.IsRecommended);
            card.Opacity = availability.IsDisabled ? 0.45 : 1;
            card.Cursor = availability.IsDisabled ? Cursors.Arrow : Cursors.Hand;
            card.ToolTip = availability.Warning;
        }
    }

    private void UpdateTemplateStatus()
    {
        var warning = _availability[_template].Warning;
        TemplateWarningText.Text = warning ?? "";
        TemplateWarningText.Visibility = Ui.Shown(warning is not null);

        var model = OnboardingTemplateRecommender.Plan(_template).Model;
        bool ready = ModelDownloader.IsCached(model);
        TemplateStatusText.Text = ready
            ? "Model ready"
            : ModelDownloader.StateOnDisk(model) is ModelDownloadState.Paused paused
                ? $"{paused.StatusText} · resumes on Continue"
                : $"Will download {model.ApproxSizeBytes / 1_000_000_000.0:0.#} GB on Continue";
        TemplateStatusText.SetResourceReference(TextBlock.ForegroundProperty, ready ? "GreenBrush" : "TextSecondary");
    }

    /// <summary>
    /// Applies the starting point as upstream does: the local engine, its model and completion
    /// length, multi-line off, and clipboard context on for every tier but Quick.
    /// </summary>
    private void CommitTemplate()
    {
        var plan = OnboardingTemplateRecommender.Plan(_template);
        // Every tier's range is one of the curated presets.
        var preset = WordCountPreset.All.First(p => p.Range == (plan.LowWords, plan.HighWords));
        _host.Update(s =>
        {
            s.Engine = EngineKind.Local;
            AppHost.PickModel(s, plan.Model.Id);
            s.CompletionLengthPreset = preset.Id;
            s.UseCustomWordRange = false;
            s.AllowMultiLine = plan.AllowMultiLine;
            s.ClipboardContext = plan.ClipboardContext;
        });
        StartModelEnsure(plan.Model.Id);
    }

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

        // A copy of this window that was closed during its download leaves the download running.
        // Following it shows its progress, where starting a second one would fail.
        var running = _host.RunningSetupDownload is { } current && current.ModelId == modelId && !current.Completion.IsCompleted
            ? current
            : null;
        if (_modelCts != running?.Cancellation) _modelCts?.Cancel();
        var seq = ++_ensureSeq;
        _lastEnsureFor = modelId;
        _ensureBusy = true;
        _modelReadyFor = null;
        _modelError = null;
        _modelPercent = null;
        UpdateModelStatusUi();

        void OnProgress(object? sender, ModelDownloader.Progress p)
        {
            if (seq != _ensureSeq) return;
            if (p.TotalBytes is long total && total > 0)
            {
                _modelPercent = (int)(p.BytesDownloaded * 100 / total);
            }
            UpdateModelStatusUi();
        }

        var download = running;
        if (download is null)
        {
            var cts = new CancellationTokenSource();
            var progress = new Progress<ModelDownloader.Progress>();
            download = new AppHost.SetupDownload(modelId, cts, _host.EnsureModelReadyAsync(progress, cts.Token), progress);
            _host.RunningSetupDownload = download;
        }
        _modelCts = download.Cancellation;
        download.Progress.ProgressChanged += OnProgress;
        try
        {
            await download.Completion;
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
        finally
        {
            download.Progress.ProgressChanged -= OnProgress;
        }
        if (ReferenceEquals(_host.RunningSetupDownload, download)) _host.RunningSetupDownload = null;
        UpdateModelStatusUi();
    }

    private void UpdateModelStatusUi()
    {
        bool ready = _modelReadyFor is not null;
        bool busy = !ready && _modelError is null && _ensureBusy;
        // A download that stopped with its partial kept shows as paused and can be resumed here.
        var paused = !ready && !_ensureBusy && _lastEnsureFor is { } id && ModelCatalog.FindById(id) is { } model
            ? ModelDownloader.StateOnDisk(model) as ModelDownloadState.Paused
            : null;
        DoneSpinner.Visibility = Ui.Shown(busy);
        DoneCheck.Visibility = Ui.Shown(ready);
        DoneResumeButton.Visibility = Ui.Shown(paused is not null);
        DoneModelStatus.SetResourceReference(TextBlock.ForegroundProperty, ready ? "GreenBrush" : "TextSecondary");
        DoneModelStatus.Text =
            ready ? "Your model is ready"
            : paused is { Progress: { } fraction } ? $"Model download paused ({ModelDownloadState.Percent(fraction)}%)"
            : paused is not null ? "Model download paused"
            : _modelError is not null ? "Model setup didn't finish. You can retry from Settings."
            : !busy ? ""
            : _modelPercent is int percent ? $"Downloading your model… {percent}%"
            : "Preparing your model…";
    }

    private void OnResumeModel(object sender, RoutedEventArgs e)
    {
        if (_lastEnsureFor is { } id) StartModelEnsure(id);
    }

    private void StartSpinner()
    {
        // The spinner is mostly hidden. One endless animation is cheaper than
        // starting and stopping it on every status change.
        var spin = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.1)) { RepeatBehavior = RepeatBehavior.Forever };
        DoneSpinnerRotate.BeginAnimation(RotateTransform.AngleProperty, spin);
        // Removed on close so the endless clock never outlives the window.
        Closed += (_, _) => DoneSpinnerRotate.BeginAnimation(RotateTransform.AngleProperty, null);
    }

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
        _host.Update(change);
        RefreshShortcuts();
    }

    /// <summary>Refreshes every editor, because one binding can change how another one reads.</summary>
    private void RefreshShortcuts()
    {
        foreach (var editor in _shortcuts) editor.Refresh();
    }

    private void OnModeClicked(object sender, RoutedEventArgs e)
    {
        _host.Update(s => s.AcceptanceMode = ModePhrase.IsChecked == true
            ? AcceptanceGranularity.Phrase
            : AcceptanceGranularity.Word);
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
