using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Cotabby.App.Hosting;
using Cotabby.Core.Models;
using Cotabby.Inference;
using Microsoft.Win32;

namespace Cotabby.App.UI;

/// <summary>
/// First-run wizard matching the macOS original: Welcome → starting-point
/// templates → profile → keybinds → done. The model download kicks off in the
/// background the moment the user confirms a template so the remaining steps
/// never block on the network; the Done step surfaces the progress instead.
/// </summary>
public partial class WelcomeWindow : Window
{
    private readonly AppHost _host;

    private int _step;
    private FrameworkElement[] _steps = Array.Empty<FrameworkElement>();
    private readonly List<Border> _capsules = new();

    // Guards persist-on-change handlers during initial population.
    private bool _loaded;

    private string _templateModelId = "tabby-2-mini";
    private static readonly string[] TemplateModelIds = { "tabby-2-nano", "tabby-2-mini", "tabby-2-base" };

    // Background model prep state. The sequence number lets a superseded run
    // (user went back and picked a different template) drop its late results.
    private CancellationTokenSource? _modelCts;
    private int _ensureSeq;
    private bool _ensureBusy;
    private string? _modelReadyFor;
    private string? _modelError;
    private int? _modelPercent;

    private static readonly string[] SuggestedLanguages = { "German", "French", "Spanish", "Chinese" };

    public WelcomeWindow(AppHost host)
    {
        _host = host;
        InitializeComponent();

        _steps = new FrameworkElement[] { StepWelcome, StepTemplates, StepProfile, StepKeys, StepDone };

        ApplyTheme();
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        SourceInitialized += (_, _) => ApplyTitleBarTheme(IsSystemLight());
        Closed += (_, _) => SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;

        TryLoadLogo();
        BuildCapsules();
        StartSpinner();

        // Seed from current settings so re-running the wizard from the tray is
        // non-destructive.
        var s = _host.Settings;
        WelcomeNameBox.Text = s.UserName;
        BuildLanguageChips();
        ModePhrase.IsChecked = s.AcceptanceMode == "phrase";
        ModeWord.IsChecked = s.AcceptanceMode != "phrase";
        _templateModelId = TemplateModelIds.Contains(s.ActiveModelId ?? "")
            ? s.ActiveModelId!
            : "tabby-2-mini";
        UpdateTemplateVisuals();

        _loaded = true;
        ShowStep(0);
    }

    public static void ShowFor(AppHost host)
    {
        var w = new WelcomeWindow(host);
        w.Show();
    }

    // ---------- Theme ----------

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e) =>
        Dispatcher.BeginInvoke(ApplyTheme);

    private void ApplyTheme()
    {
        bool light = IsSystemLight();
        SetBrush("WindowBg", light ? "#F5F5F7" : "#1F1F1F");
        SetBrush("CardBg", light ? "#FFFFFF" : "#2A2A2A");
        SetBrush("CardBorder", light ? "#E3E3E3" : "#3D3D3D");
        SetBrush("TextPrimary", light ? "#1D1D1F" : "#E8E8E8");
        SetBrush("TextSecondary", light ? "#6E6E73" : "#9A9A9A");
        SetBrush("InputBg", light ? "#F2F2F4" : "#333333");
        SetBrush("KeycapBg", light ? "#EFEFF1" : "#3A3A3A");
        SetBrush("TrackOff", light ? "#D1D1D6" : "#4A4A4A");
        ApplyTitleBarTheme(light);
        UpdateCapsules();
    }

    private void SetBrush(string key, string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        Resources[key] = brush;
    }

    private static bool IsSystemLight()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v != 0;
        }
        catch
        {
            return false;
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private void ApplyTitleBarTheme(bool light)
    {
        // Without DWMWA_USE_IMMERSIVE_DARK_MODE the title bar stays white on a
        // dark window. Cosmetic only, so failures are ignored.
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            int dark = light ? 0 : 1;
            _ = DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));
        }
        catch { /* pre-1809 builds lack the attribute */ }
    }

    private void TryLoadLogo()
    {
        try
        {
            var logo = new BitmapImage(new Uri("pack://application:,,,/Assets/CotabbyLogo.png"));
            LogoBorder.Background = new ImageBrush(logo) { Stretch = Stretch.UniformToFill };
            LogoFallback.Visibility = Visibility.Collapsed;
        }
        catch { /* asset ships separately — the blue-square cat fallback stays */ }
    }

    // ---------- Step navigation ----------

    private void BuildCapsules()
    {
        CapsulePanel.Children.Clear();
        _capsules.Clear();
        for (int i = 0; i < _steps.Length; i++)
        {
            var capsule = new Border
            {
                Height = 6,
                Width = 18,
                CornerRadius = new CornerRadius(3),
                Margin = new Thickness(3, 0, 3, 0),
            };
            _capsules.Add(capsule);
            CapsulePanel.Children.Add(capsule);
        }
        UpdateCapsules();
    }

    private void UpdateCapsules()
    {
        for (int i = 0; i < _capsules.Count; i++)
        {
            _capsules[i].Width = i == _step ? 28 : 18;
            if (i <= _step)
            {
                _capsules[i].Background = (Brush)FindResource("AccentBrush");
            }
            else
            {
                _capsules[i].SetResourceReference(Border.BackgroundProperty, "TrackOff");
            }
        }
    }

    private void ShowStep(int idx)
    {
        _step = Math.Clamp(idx, 0, _steps.Length - 1);
        for (int i = 0; i < _steps.Length; i++)
        {
            _steps[i].Visibility = i == _step ? Visibility.Visible : Visibility.Collapsed;
        }

        StepCaption.Text = $"Step {_step + 1} of {_steps.Length}";
        ContinueButton.Content = _step switch
        {
            0 => "Get Started",
            4 => "Start Using Cotabby",
            _ => "Continue",
        };
        // Hidden (not collapsed) so the footer height never jumps between steps.
        BackButton.Visibility = _step == 0 ? Visibility.Hidden : Visibility.Visible;
        SkipButton.Visibility = _step == _steps.Length - 1 ? Visibility.Hidden : Visibility.Visible;

        UpdateCapsules();
        if (_step == 1) UpdateTemplateStatus();
        if (_step == 4) UpdateModelStatusUi();
    }

    private void OnBack(object sender, RoutedEventArgs e) => ShowStep(_step - 1);

    private void OnContinue(object sender, RoutedEventArgs e)
    {
        switch (_step)
        {
            case 1:
                CommitTemplate();
                break;
            case 2:
                // Name commits when leaving the profile step (and again at
                // finish, in case the user went back to edit).
                _host.Settings.UserName = WelcomeNameBox.Text?.Trim() ?? "";
                break;
            case 4:
                Finish();
                return;
        }
        ShowStep(_step + 1);
    }

    private void OnSkip(object sender, RoutedEventArgs e)
    {
        // Skip preserves whatever the user already changed; only the first-run
        // flag is cleared so we don't pester them on next launch.
        _host.Settings.ShowFirstRunWelcome = false;
        _host.PersistSettings();
        Close();
    }

    private void Finish()
    {
        var s = _host.Settings;
        s.UserName = WelcomeNameBox.Text?.Trim() ?? "";
        if (s.Languages.Count == 0) s.Languages.Add("English");
        s.ShowFirstRunWelcome = false;
        _host.PersistSettings();
        // Any in-flight model download keeps running on the host; the tray app
        // stays alive after this window closes.
        Close();
    }

    // ---------- Templates ----------

    private void OnTemplateClicked(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border { Tag: string modelId })
        {
            _templateModelId = modelId;
            UpdateTemplateVisuals();
            UpdateTemplateStatus();
        }
    }

    private void UpdateTemplateVisuals()
    {
        var cards = new (Border Card, Border Badge, string Id)[]
        {
            (QuickCard, QuickBadge, "tabby-2-nano"),
            (EverydayCard, EverydayBadge, "tabby-2-mini"),
            (PowerCard, PowerBadge, "tabby-2-base"),
        };
        foreach (var (card, badge, id) in cards)
        {
            bool selected = id == _templateModelId;
            if (selected)
            {
                card.BorderBrush = (Brush)FindResource("AccentBrush");
                badge.Background = (Brush)FindResource("AccentTint");
            }
            else
            {
                card.SetResourceReference(Border.BorderBrushProperty, "CardBorder");
                badge.SetResourceReference(Border.BackgroundProperty, "KeycapBg");
            }
        }
    }

    private void UpdateTemplateStatus()
    {
        var model = ModelCatalog.FindById(_templateModelId);
        if (model is null)
        {
            TemplateStatusText.Text = "";
            return;
        }
        bool ready = ModelDownloader.IsCached(model);
        if (ready)
        {
            TemplateStatusText.Text = "Model ready";
            TemplateStatusText.Foreground = (Brush)FindResource("GreenBrush");
        }
        else
        {
            TemplateStatusText.Text = $"Will download {FormatGb(model.ApproxSizeBytes)} on Continue";
            TemplateStatusText.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
        }
    }

    private static string FormatGb(long bytes) => $"{bytes / 1_000_000_000.0:0.#} GB";

    private void CommitTemplate()
    {
        _host.Settings.ActiveModelId = _templateModelId;
        _host.PersistSettings();
        StartModelEnsure(_templateModelId);
    }

    // ---------- Background model prep ----------

    private async void StartModelEnsure(string modelId)
    {
        if (_modelReadyFor == modelId) { UpdateModelStatusUi(); return; }
        // Re-confirming the same template must not restart an in-flight download.
        if (_ensureBusy && _lastEnsureFor == modelId) return;

        // Skip the round-trip when the runtime already holds this model.
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

    private string? _lastEnsureFor;

    private void UpdateModelStatusUi()
    {
        if (_modelReadyFor is not null)
        {
            DoneSpinner.Visibility = Visibility.Collapsed;
            DoneCheck.Visibility = Visibility.Visible;
            DoneModelStatus.Text = "Your model is ready";
            DoneModelStatus.Foreground = (Brush)FindResource("GreenBrush");
            return;
        }
        if (_modelError is not null)
        {
            DoneSpinner.Visibility = Visibility.Collapsed;
            DoneCheck.Visibility = Visibility.Collapsed;
            DoneModelStatus.Text = "Model setup didn't finish — you can retry from Settings.";
            DoneModelStatus.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            return;
        }
        if (_ensureBusy)
        {
            DoneSpinner.Visibility = Visibility.Visible;
            DoneCheck.Visibility = Visibility.Collapsed;
            DoneModelStatus.Text = _modelPercent is int pct
                ? $"Downloading your model… {pct}%"
                : "Preparing your model…";
            DoneModelStatus.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            return;
        }
        DoneSpinner.Visibility = Visibility.Collapsed;
        DoneCheck.Visibility = Visibility.Collapsed;
        DoneModelStatus.Text = "";
    }

    private void StartSpinner()
    {
        // The spinner is mostly hidden; one forever-animation is cheaper than
        // starting and stopping it on every status flip.
        var spin = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.1))
        {
            RepeatBehavior = RepeatBehavior.Forever,
        };
        DoneSpinnerRotate.BeginAnimation(RotateTransform.AngleProperty, spin);
    }

    // ---------- Profile ----------

    private void BuildLanguageChips()
    {
        WelcomeLanguageChips.Children.Clear();
        foreach (var lang in _host.Settings.Languages.ToList())
        {
            var name = lang;
            WelcomeLanguageChips.Children.Add(BuildChip(name, () => RemoveLanguage(name)));
        }

        WelcomeLanguageSuggestions.Children.Clear();
        foreach (var lang in SuggestedLanguages)
        {
            if (_host.Settings.Languages.Any(l => l.Equals(lang, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }
            var name = lang;
            WelcomeLanguageSuggestions.Children.Add(BuildSuggestionChip(name, () => AddLanguage(name)));
        }
    }

    private FrameworkElement BuildChip(string text, Action remove)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
        });
        var close = new TextBlock
        {
            Text = "",
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 9,
            Margin = new Thickness(7, 1, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.Hand,
            ToolTip = "Remove",
        };
        close.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
        close.MouseLeftButtonUp += (_, e) => { e.Handled = true; remove(); };
        content.Children.Add(close);

        var chip = new Border
        {
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 4, 8, 4),
            Margin = new Thickness(0, 0, 6, 6),
            Child = content,
        };
        chip.SetResourceReference(Border.BackgroundProperty, "KeycapBg");
        chip.SetResourceReference(Border.BorderBrushProperty, "CardBorder");
        return chip;
    }

    private FrameworkElement BuildSuggestionChip(string label, Action add)
    {
        var cell = new Grid
        {
            Margin = new Thickness(0, 0, 6, 6),
            Cursor = Cursors.Hand,
            Background = Brushes.Transparent,
        };
        // Border can't dash its stroke, so the chip outline is a Rectangle.
        var outline = new Rectangle
        {
            RadiusX = 12,
            RadiusY = 12,
            StrokeThickness = 1,
            StrokeDashArray = new DoubleCollection { 3, 2 },
            Fill = Brushes.Transparent,
        };
        outline.SetResourceReference(Shape.StrokeProperty, "TextSecondary");
        var text = new TextBlock
        {
            Text = "+ " + label,
            FontSize = 12,
            Margin = new Thickness(10, 4, 10, 4),
        };
        text.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
        cell.Children.Add(outline);
        cell.Children.Add(text);
        cell.MouseLeftButtonUp += (_, _) => add();
        return cell;
    }

    private void AddLanguage(string? raw)
    {
        var lang = raw?.Trim();
        if (string.IsNullOrEmpty(lang)) return;
        var s = _host.Settings;
        if (s.Languages.Count >= 6) return;
        if (s.Languages.Any(l => l.Equals(lang, StringComparison.OrdinalIgnoreCase))) return;
        s.Languages.Add(lang);
        _host.PersistSettings();
        BuildLanguageChips();
    }

    private void RemoveLanguage(string lang)
    {
        var s = _host.Settings;
        s.Languages.RemoveAll(l => l.Equals(lang, StringComparison.OrdinalIgnoreCase));
        // The prompt renderer expects at least one language.
        if (s.Languages.Count == 0) s.Languages.Add("English");
        _host.PersistSettings();
        BuildLanguageChips();
    }

    private void OnAddLanguage(object sender, RoutedEventArgs e)
    {
        AddLanguage(WelcomeLanguageBox.Text);
        WelcomeLanguageBox.Text = "";
    }

    private void OnLanguageBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        AddLanguage(WelcomeLanguageBox.Text);
        WelcomeLanguageBox.Text = "";
    }

    // ---------- Keybinds ----------

    private void OnModeChanged(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        _host.Settings.AcceptanceMode = ModePhrase.IsChecked == true ? "phrase" : "word";
        _host.PersistSettings();
    }
}
