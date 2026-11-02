using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Cotabby.App.Settings;
using Cotabby.App.UI;
using Cotabby.Core.Context;
using Cotabby.Core.Focus;
using Cotabby.Core.Input;
using Cotabby.Core.Overlay;
using Cotabby.Core.Overlay.Font;
using Microsoft.Win32;

namespace Cotabby.App.Overlay;

/// <summary>
/// Transparent, topmost, click-through window that renders ghost text at the caret.
/// </summary>
public partial class GhostOverlayWindow : Window, IOverlayPresenter
{
    private const double FontSizePerCaretHeight = 0.78;
    private const double CardFontSize = 14;
    private const double CardGapPx = 4;
    private const double PointsToDips = 96.0 / 72.0;

    /// <summary>A band narrower than this many DIPs is not worth wrapping into.</summary>
    private const double MinimumWrapWidthDip = 120;

    private static readonly Thickness CardPadding = new(10, 6, 10, 6);
    private static readonly Color CorrectionColor = Color.FromRgb(0x22, 0xC5, 0x5E);
    private static readonly Color CardTextColor = Color.FromRgb(0xE8, 0xE8, 0xE8);
    private static readonly Brush CardFill = Frozen(Color.FromArgb(0xF2, 0x1E, 0x1E, 0x1E));
    private static readonly Brush CardStroke = Frozen(Color.FromRgb(0x4D, 0x4D, 0x4D));

    /// <summary>Office hosts, whose document fonts may come from the per-user cloud font cache.</summary>
    private static readonly HashSet<string> OfficeProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "WINWORD", "EXCEL", "POWERPNT", "OUTLOOK", "ONENOTE", "olk", "MSACCESS", "MSPUB",
    };

    private readonly WpfGlyphMeasurer _measurer = new();
    private readonly FontFamily _defaultFamily;

    private HwndSource? _source;
    private Color? _userColor;
    private double _opacity = 1.0;
    private double _sizeMultiplier = 1.0;
    private double _sizeFloor = GhostFontSizeLimits.DefaultFloor;
    private double _sizeCeiling = GhostFontSizeLimits.DefaultCeiling;
    private bool _fadeInEnabled;
    private Duration _fadeIn = Duration.Automatic;
    private OverlayStyle _style;

    // UIA flips between the real line height and a coarser fallback while a suggestion
    // is visible. Holding the larger reading keeps the ghost from shrinking mid-suggestion.
    private double _stableCaretHeightPx;

    // The field the current show was anchored in, when the caller passed one.
    private FocusedField? _field;
    // The host's face and size in DIPs before the user's size setting, or null for the default look.
    private (GhostFace Face, double Size)? _hostFont;
    // A host-matched size only grows within one field and host font, so the ghost never shrinks
    // under the user while samples come and go.
    private string? _fontKey;
    private double _heldFontSize;
    private HostAdvanceFit _advanceFit = new();
    private AppSettings? _settings;
    private ScreenRect _anchor;
    private bool _rightToLeft;

    public GhostOverlayWindow()
    {
        InitializeComponent();
        _defaultFamily = GhostText.FontFamily;
        Visibility = Visibility.Hidden;
        SourceInitialized += OnSourceInitialized;
        SystemEvents.UserPreferenceChanged += (_, _) => Dispatcher.BeginInvoke(ApplyPalette);
        ApplyPalette();
    }

    public void ApplyAppearance(AppSettings settings)
    {
        _settings = settings;
        _userColor = GhostTextPalette.Resolve(settings.GhostTextColor);
        _opacity = Math.Clamp(settings.GhostTextOpacity, 0.1, 1.0);
        _sizeMultiplier = Math.Clamp(settings.GhostTextSizeMultiplier, 0.5, 2.0);
        (_sizeFloor, _sizeCeiling) = GhostFontSizeLimits.Normalized(settings.GhostFontSizeFloor, settings.GhostFontSizeCeiling);
        _fadeInEnabled = settings.FadeInSuggestions && settings.FadeInSeconds > 0;
        _fadeIn = _fadeInEnabled
            ? new Duration(TimeSpan.FromSeconds(settings.FadeInSeconds))
            : Duration.Automatic;
        _fontKey = null;
        ApplyPalette();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _source = (HwndSource)PresentationSource.FromVisual(this)!;
        OverlayWindowInterop.MakeClickThrough(_source.Handle);
    }

    public void Show(
        string text, ScreenRect anchor, OverlayStyle style = OverlayStyle.Inline, FocusedField? field = null)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => Show(text, anchor, style, field));
            return;
        }

        bool wasVisible = Visibility == Visibility.Visible;
        _field = field;
        _anchor = anchor;
        _rightToLeft = TextDirectionDetector.IsRightToLeft(field?.PrecedingText);
        TabKeycapLabel.Text = HintLabel(field?.ProcessName);
        UseStyle(style);
        ApplyFont(anchor);
        Visibility = Visibility.Visible;
        Render(text);
        if (SuggestionFadeInPolicy.ShouldFadeIn(_fadeInEnabled, wasVisible, !SystemParameters.ClientAreaAnimation))
        {
            GhostRow.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, _fadeIn));
        }
    }

    /// <summary>Lays out the remaining text again at the last anchor until the next show moves it.</summary>
    public void Update(string text)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => Update(text));
            return;
        }
        Render(text);
    }

    public new void Hide()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(Hide);
            return;
        }
        SetRows([""]);
        _stableCaretHeightPx = 0;
        Visibility = Visibility.Hidden;
    }

    private string HintLabel(string? processName) =>
        _settings is { ShowAcceptanceHint: true } s
            ? ShortcutResolver.HintLabel(
                processName, s.ShortcutOverrides, s.AcceptWord, s.DoubleTapAcceptsAll ? KeyBinding.Unbound : s.AcceptAll) ?? ""
            : "";

    private void UseStyle(OverlayStyle style)
    {
        if (_style == style) return;
        _style = style;
        ApplyPalette();
    }

    private void ApplyFont(ScreenRect anchor)
    {
        // A card is not lined up with the host's text, so it keeps one readable size.
        if (_style != OverlayStyle.Inline)
        {
            UseDefaultFace();
            GhostText.FontSize = GhostFontSizeLimits.Clamped(CardFontSize * _sizeMultiplier, _sizeFloor, _sizeCeiling);
            return;
        }
        if (anchor.Height <= 1) return;

        if (_field is { } field && ReportsHostText(field) && ResolveHostFont(field, anchor) is { } font)
        {
            _hostFont = font;
            GhostText.FontFamily = _measurer.Family(font.Face.Family);
            GhostText.FontWeight = FontWeight.FromOpenTypeWeight(Math.Clamp(font.Face.Weight, 1, 999));
            GhostText.FontStyle = font.Face.Italic ? FontStyles.Italic : FontStyles.Normal;
            GhostText.FontSize = Math.Round(
                GhostFontSizeLimits.Clamped(font.Size * _sizeMultiplier, _sizeFloor, _sizeCeiling), 2);
            // Ideal layout keeps the advances the match was measured with.
            TextOptions.SetTextFormattingMode(GhostText, TextFormattingMode.Ideal);
            return;
        }

        UseDefaultFace();
        _stableCaretHeightPx = Math.Max(_stableCaretHeightPx, anchor.Height);
        double scale = _source?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;
        double caretHeightDip = _stableCaretHeightPx / Math.Max(scale, 0.1);
        GhostText.FontSize = Math.Round(GhostFontSizeLimits.Clamped(
            caretHeightDip * FontSizePerCaretHeight * _sizeMultiplier, _sizeFloor, _sizeCeiling), 1);
    }

    private void UseDefaultFace()
    {
        _hostFont = null;
        GhostText.FontFamily = _defaultFamily;
        GhostText.FontWeight = FontWeights.Normal;
        GhostText.FontStyle = FontStyles.Normal;
        TextOptions.SetTextFormattingMode(GhostText, TextFormattingMode.Display);
    }

    private static bool ReportsHostText(FocusedField field) =>
        field.HostFontName is not null || field.HostFontSizePt is not null || field.SampleWidthPx is > 0;

    /// <summary>The host's face and size in DIPs, from what the host reported about its text.</summary>
    private (GhostFace Face, double Size)? ResolveHostFont(FocusedField field, ScreenRect anchor)
    {
        try
        {
            double scale = OverlayWindowInterop.DipScaleAt(anchor.X, anchor.Y);
            _measurer.UseOfficeCloudFonts = OfficeProcesses.Contains(field.ProcessName);
            var resolution = GhostFontResolver.Resolve(new GhostFontInput
            {
                FontName = field.HostFontName,
                ReportedSize = field.HostFontSizePt * PointsToDips,
                Weight = field.HostFontWeight,
                Italic = field.HostIsItalic,
                CaretBoxHeight = field.CaretIsEstimated ? 0 : anchor.Height / scale,
                SampleText = field.SampleText,
                SampleWidth = field.SampleWidthPx / scale,
                Renderer = BrowserAppDetector.NeedsWebAccessibilityPriming(field.ProcessName)
                    ? HostRenderer.Web
                    : HostRenderer.Native,
                ZoomLadder = HostZoomLadder.For(field.ProcessName),
            }, _measurer);

            string key = $"{field.FieldId}|{field.HostFontName}|{field.HostFontSizePt}|{resolution.Face}";
            if (key != _fontKey)
            {
                _fontKey = key;
                _heldFontSize = 0;
                _advanceFit = new HostAdvanceFit();
            }

            // A face the host never sized is refined by where its caret lands along the line, and a
            // fit is a measurement, so it replaces the held size even when smaller.
            if (resolution.Provenance is GhostFontProvenance.CaretDerived or GhostFontProvenance.CaretDerivedCalibrated
                && !field.CaretIsEstimated && field.SampleText is { Length: > 0 } sample && !field.CaretLineRect.IsEmpty)
            {
                _advanceFit.Record(
                    sample, anchor.X / scale, $"{field.CaretLineRect.X:0}|{field.CaretLineRect.Y:0}",
                    resolution.Face, resolution.Size, _measurer,
                    field.IsSingleLine ? HostAdvanceFit.SingleLineMinimumSpan : HostAdvanceFit.MinimumSpan);
            }
            _heldFontSize = _advanceFit.Adopted is { } fit ? fit.Size : Math.Max(_heldFontSize, resolution.Size);
            return (resolution.Face, _heldFontSize);
        }
        catch (Exception) { return null; }
    }

    private void ApplyPalette()
    {
        bool dark = SystemTheme.IsDark;
        bool card = _style != OverlayStyle.Inline;
        Card.Background = card ? CardFill : Brushes.Transparent;
        Card.BorderBrush = card ? CardStroke : Brushes.Transparent;
        Card.Padding = card ? CardPadding : default;

        var text = _style switch
        {
            OverlayStyle.Correction => CorrectionColor,
            OverlayStyle.Popup => CardTextColor,
            _ => _userColor ?? (dark ? Color.FromRgb(0xA6, 0xA6, 0xA6) : Color.FromRgb(0x73, 0x73, 0x73)),
        };
        byte alpha = card ? (byte)0xFF : (byte)Math.Round(_opacity * 0xCC);
        GhostText.Foreground = new SolidColorBrush(Color.FromArgb(alpha, text.R, text.G, text.B));

        TabKeycap.Background = Brush(dark ? 0x2E : 0xF2);
        TabKeycap.BorderBrush = Brush(dark ? 0x4D : 0xCC);
        TabKeycapLabel.Foreground = Brush(dark ? 0xA6 : 0x73);

        static SolidColorBrush Brush(int gray) => new(Color.FromRgb((byte)gray, (byte)gray, (byte)gray));
    }

    private void Render(string text)
    {
        if (_source?.CompositionTarget is not { } target || text.Length == 0)
        {
            SetRows([text]);
            TabKeycap.Visibility = Visibility.Collapsed;
            return;
        }
        double scaleX = target.TransformToDevice.M11;
        double scaleY = target.TransformToDevice.M22;
        var area = OverlayWindowInterop.WorkAreaAt(_anchor.X, _anchor.Y);
        if (_style == OverlayStyle.Inline && !TryRenderInline(text, area, scaleX, scaleY))
        {
            // No honest inline layout exists for this text here, so the card shows it.
            UseStyle(OverlayStyle.Popup);
            ApplyFont(_anchor);
        }
        if (_style != OverlayStyle.Inline) RenderCard(text, area, scaleX, scaleY);
    }

    private bool TryRenderInline(string text, ScreenRect area, double scaleX, double scaleY)
    {
        var anchor = _anchor;
        double caretX = LaggedCaretX(anchor);
        var (left, right) = BandFor(anchor, area, scaleX);
        double firstRowDip = (right - caretX - 1) / scaleX;
        double bandDip = (right - left) / scaleX;
        double indentDip = (caretX + 1 - left) / scaleX;
        // A second row would cover the host's following lines, and a single-line field scrolls instead.
        bool canWrap = !_rightToLeft && _field is { IsSingleLine: false, FieldRect.IsEmpty: false } field
            && string.IsNullOrWhiteSpace(field.TrailingText)
            && bandDip >= MinimumWrapWidthDip && indentDip >= 0 && indentDip < bandDip;

        var layout = GhostRowLayout.Make(
            new GhostRowLayout.Input(text, firstRowDip, canWrap ? bandDip : null, KeycapWidthDip(), _rightToLeft),
            MeasureDip);
        if (layout is null) return false;

        bool wrapped = layout.Rows.Count > 1;
        double rowHeightDip = Math.Max(anchor.Height / scaleY, GhostText.FontSize);
        SetRows(layout.Rows, wrapped ? indentDip : 0, rowHeightDip);
        TabKeycap.Visibility = layout.ShowsKeycap && !wrapped ? Visibility.Visible : Visibility.Collapsed;
        UpdateLayout();

        double width = ActualWidth * scaleX;
        double height = ActualHeight * scaleY;
        // Inline text continues the line at the caret, on its left for right-to-left text. Wrapped
        // rows start at the band's left edge with the first indented to the caret.
        double x = wrapped ? left : _rightToLeft ? caretX - width - 1 : caretX + 1;
        double y = anchor.Y + Math.Max(0, (anchor.Height - (wrapped ? rowHeightDip * scaleY : height)) / 2.0);
        Place(x, y, width, height, area);
        return true;
    }

    private void RenderCard(string text, ScreenRect area, double scaleX, double scaleY)
    {
        SetRows([GhostCardPlacement.CardText(text)]);
        TabKeycap.Visibility = TabKeycapLabel.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateLayout();

        double width = ActualWidth * scaleX;
        double height = ActualHeight * scaleY;
        var fieldRect = _field?.FieldRect ?? ScreenRect.Empty;
        double inset = (CardPadding.Left + Card.BorderThickness.Left) * scaleX;
        Place(
            GhostCardPlacement.Left(_anchor, fieldRect, width, inset, _rightToLeft),
            GhostCardPlacement.Top(_anchor, fieldRect, CardGapPx),
            width, height, area);
    }

    /// <summary>Places the overlay, kept inside the work area of the monitor under the caret.</summary>
    private void Place(double x, double y, double width, double height, ScreenRect area)
    {
        if (x + width > area.Right) x = area.Right - width - 2;
        if (x < area.X) x = area.X + 2;
        if (y + height > area.Bottom) y = _anchor.Y - height - 2;
        if (y < area.Y) y = area.Y + 2;
        OverlayWindowInterop.PlaceTopmost(_source!.Handle, x, y, width, height);
    }

    /// <summary>
    /// Shows the rows as the host would lay them out: the first indented to the caret and each next
    /// one a host line further down. A break's hanging spaces are left off.
    /// </summary>
    private void SetRows(IReadOnlyList<string> rows, double indentDip = 0, double rowHeightDip = double.NaN)
    {
        if (rows.Count == 1)
        {
            GhostText.Text = rows[0];
            GhostText.LineHeight = double.NaN;
            GhostText.LineStackingStrategy = LineStackingStrategy.MaxHeight;
            return;
        }
        GhostText.Inlines.Clear();
        GhostText.Inlines.Add(new InlineUIContainer(new Border { Width = indentDip, Height = 1 })
        {
            BaselineAlignment = BaselineAlignment.Baseline,
        });
        for (int i = 0; i < rows.Count; i++)
        {
            if (i > 0) GhostText.Inlines.Add(new LineBreak());
            GhostText.Inlines.Add(new Run(i < rows.Count - 1 ? rows[i].TrimEnd() : rows[i]));
        }
        GhostText.LineHeight = rowHeightDip;
        GhostText.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
    }

    private double KeycapWidthDip()
    {
        if (TabKeycapLabel.Text.Length == 0) return 0;
        TabKeycap.Visibility = Visibility.Visible;
        TabKeycap.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return TabKeycap.DesiredSize.Width;
    }

    private double MeasureDip(string text) =>
        new FormattedText(
            text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(GhostText.FontFamily, GhostText.FontStyle, GhostText.FontWeight, FontStretches.Normal),
            GhostText.FontSize, Brushes.Black, null, TextOptions.GetTextFormattingMode(GhostText),
            VisualTreeHelper.GetDpi(this).PixelsPerDip).WidthIncludingTrailingWhitespace;

    /// <summary>
    /// The caret's x, moved past the text before it when the host has published a keystroke's text
    /// but not yet moved its caret.
    /// </summary>
    private double LaggedCaretX(ScreenRect anchor)
    {
        if (_hostFont is not { } font || _field is not { SampleText: { Length: > 0 } sample } field
            || field.CaretLineRect.IsEmpty)
        {
            return anchor.Right;
        }
        double scale = OverlayWindowInterop.DipScaleAt(anchor.X, anchor.Y);
        var line = field.CaretLineRect;
        double lineWidth = Math.Max(line.Width, field.FieldRect.Right - line.X);
        bool lags = CaretLagPolicy.CaretLagsTypedText(
            anchor.X / scale, line.X / scale, lineWidth / scale, sample, font.Face, font.Size, _measurer,
            isRightToLeft: false);
        return lags ? line.X + _measurer.Width(sample, font.Face, font.Size) * scale : anchor.Right;
    }

    private (double Left, double Right) BandFor(ScreenRect anchor, ScreenRect area, double scale)
    {
        var field = _field;
        double? lineLeft = field is { CaretLineRect.IsEmpty: false } ? field.CaretLineRect.X : null;
        return GhostWrapBandPolicy.Band(new GhostWrapBandPolicy.Input(
            anchor, field?.FieldRect ?? ScreenRect.Empty, lineLeft, area.X, area.Right, scale));
    }

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
