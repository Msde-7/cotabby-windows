using System.Runtime.InteropServices;
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
using Cotabby.Core.Overlay;
using Cotabby.Core.Overlay.Font;
using Microsoft.Win32;

namespace Cotabby.App.Overlay;

/// <summary>
/// Transparent, topmost, click-through window that renders ghost text at the caret.
/// </summary>
/// <remarks>
/// WPF positions windows in DIPs at the primary monitor's scale, which is wrong on
/// mixed-DPI setups. The window is therefore placed with <c>SetWindowPos</c> in the
/// physical pixels UI Automation reports.
/// </remarks>
public partial class GhostOverlayWindow : Window, IOverlayPresenter
{
    private const double FontSizePerCaretHeight = 0.78;
    private const double CardFontSize = 14;
    private const double CardGapPx = 4;
    private const double PointsToDips = 96.0 / 72.0;

    /// <summary>A band narrower than this many DIPs is not worth wrapping into.</summary>
    private const double MinimumWrapWidthDip = 120;

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
    private bool _showHint = true;
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
    private Run? _wrappedRun;

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
        _userColor = GhostTextPalette.Resolve(settings.GhostTextColor);
        _opacity = Math.Clamp(settings.GhostTextOpacity, 0.1, 1.0);
        _sizeMultiplier = Math.Clamp(settings.GhostTextSizeMultiplier, 0.5, 2.0);
        (_sizeFloor, _sizeCeiling) = GhostFontSizeLimits.Normalized(settings.GhostFontSizeFloor, settings.GhostFontSizeCeiling);
        _showHint = settings.ShowAcceptanceHint;
        TabKeycapLabel.Text = settings.AcceptWord.IsBound ? settings.AcceptWord.Label : "";
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
        int style = GetWindowLong(_source.Handle, GWL_EXSTYLE);
        SetWindowLong(_source.Handle, GWL_EXSTYLE,
            style | WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
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
        bool rightToLeft = TextDirectionDetector.IsRightToLeft(field?.PrecedingText);
        _field = field;
        if (_style != style)
        {
            _style = style;
            ApplyPalette();
        }
        ApplyFont(anchor);
        EndWrap();
        SetText(text);
        Visibility = Visibility.Visible;
        UpdateLayout();
        PositionAt(anchor, rightToLeft);
        if (SuggestionFadeInPolicy.ShouldFadeIn(_fadeInEnabled, wasVisible, !SystemParameters.ClientAreaAnimation))
        {
            GhostRow.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, _fadeIn));
        }
    }

    public void Update(string text)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => Update(text));
            return;
        }
        SetText(text);
        UpdateLayout();
    }

    public new void Hide()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(Hide);
            return;
        }
        SetText(string.Empty);
        _stableCaretHeightPx = 0;
        Visibility = Visibility.Hidden;
    }

    private void SetText(string text)
    {
        if (_wrappedRun is not null && text.Length > 0)
        {
            _wrappedRun.Text = text;
        }
        else
        {
            EndWrap();
            GhostText.Text = text;
        }
        TabKeycap.Visibility = _showHint && _wrappedRun is null && text.Length > 0 && TabKeycapLabel.Text.Length > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
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
            double scale = DipScaleAt(anchor);
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
        Card.Padding = card ? new Thickness(10, 6, 10, 6) : default;

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

    private void PositionAt(ScreenRect anchor, bool rightToLeft)
    {
        if (_source?.CompositionTarget is not { } target) return;

        double scaleX = target.TransformToDevice.M11;
        double scaleY = target.TransformToDevice.M22;
        double width = ActualWidth * scaleX;
        double height = ActualHeight * scaleY;
        var area = WorkAreaAt(anchor);
        bool card = _style != OverlayStyle.Inline;

        double caretX = card ? anchor.X : LaggedCaretX(anchor);
        // Inline text continues the line at the caret, on its left for right-to-left text.
        // A card sits just under the caret's line.
        double x = card ? anchor.X : rightToLeft ? caretX - width - 1 : caretX + 1;
        double y = card ? anchor.Bottom + CardGapPx : anchor.Y + Math.Max(0, (anchor.Height - height) / 2.0);

        // Text that runs past the field wraps inside it instead of sliding back over the host's text.
        if (!card && !rightToLeft && x + width > BandFor(anchor, area, scaleX).Right
            && TryWrap(anchor, area, caretX, scaleX, scaleY, out var wrapped))
        {
            (x, y, width, height) = wrapped;
        }

        // Keep the whole overlay inside the work area of the monitor under the caret.
        if (x + width > area.right) x = area.right - width - 2;
        if (x < area.left) x = area.left + 2;
        if (y + height > area.bottom) y = anchor.Y - height - 2;
        if (y < area.top) y = area.top + 2;

        SetWindowPos(_source.Handle, HWND_TOPMOST, (int)x, (int)y, (int)width, (int)height,
            SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

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
        double scale = DipScaleAt(anchor);
        var line = field.CaretLineRect;
        double lineWidth = Math.Max(line.Width, field.FieldRect.Right - line.X);
        bool lags = CaretLagPolicy.CaretLagsTypedText(
            anchor.X / scale, line.X / scale, lineWidth / scale, sample, font.Face, font.Size, _measurer,
            isRightToLeft: false);
        return lags ? line.X + _measurer.Width(sample, font.Face, font.Size) * scale : anchor.Right;
    }

    private (double Left, double Right) BandFor(ScreenRect anchor, RECT area, double scale)
    {
        var field = _field;
        double? lineLeft = field is { CaretLineRect.IsEmpty: false } ? field.CaretLineRect.X : null;
        return GhostWrapBandPolicy.Band(new GhostWrapBandPolicy.Input(
            anchor, field?.FieldRect ?? ScreenRect.Empty, lineLeft, area.left, area.right, scale));
    }

    /// <summary>
    /// Lays the ghost out over several rows inside the field's band: the first row continues at the
    /// caret and later rows start at the band's left edge, as the host's own text would. Only for
    /// multi-line fields with a known frame, since a single-line field scrolls instead.
    /// </summary>
    private bool TryWrap(
        ScreenRect anchor, RECT area, double caretX, double scaleX, double scaleY,
        out (double X, double Y, double Width, double Height) placement)
    {
        placement = default;
        if (_field is not { IsSingleLine: false, FieldRect.IsEmpty: false }) return false;
        var (left, right) = BandFor(anchor, area, scaleX);
        double bandDip = (right - left) / scaleX;
        double indentDip = (caretX + 1 - left) / scaleX;
        if (bandDip < MinimumWrapWidthDip || indentDip < 0 || indentDip >= bandDip) return false;

        string text = _wrappedRun?.Text ?? GhostText.Text;
        _wrappedRun = new Run(text);
        GhostText.Inlines.Clear();
        GhostText.Inlines.Add(new InlineUIContainer(new Border { Width = indentDip, Height = 1 })
        {
            BaselineAlignment = BaselineAlignment.Baseline,
        });
        GhostText.Inlines.Add(_wrappedRun);
        GhostText.TextWrapping = TextWrapping.Wrap;
        GhostText.Width = bandDip;
        TabKeycap.Visibility = Visibility.Collapsed;
        UpdateLayout();

        double lineHeight = GhostText.FontFamily.LineSpacing * GhostText.FontSize * scaleY;
        placement = (
            left,
            anchor.Y + Math.Max(0, (anchor.Height - lineHeight) / 2.0),
            ActualWidth * scaleX,
            ActualHeight * scaleY);
        return true;
    }

    private void EndWrap()
    {
        if (_wrappedRun is null) return;
        _wrappedRun = null;
        GhostText.TextWrapping = TextWrapping.NoWrap;
        GhostText.Width = double.NaN;
    }

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    /// <summary>Physical pixels per DIP on the monitor under the caret.</summary>
    private static double DipScaleAt(ScreenRect anchor)
    {
        var monitor = MonitorFromPoint(new POINT { x = (int)anchor.X, y = (int)anchor.Y }, MONITOR_DEFAULTTONEAREST);
        return GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out uint dpiX, out _) == 0 && dpiX > 0 ? dpiX / 96.0 : 1.0;
    }

    private static RECT WorkAreaAt(ScreenRect anchor)
    {
        var monitor = MonitorFromPoint(new POINT { x = (int)anchor.X, y = (int)anchor.Y }, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        return GetMonitorInfo(monitor, ref info)
            ? info.rcWork
            : new RECT { right = (int)SystemParameters.PrimaryScreenWidth, bottom = (int)SystemParameters.PrimaryScreenHeight };
    }

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const int MDT_EFFECTIVE_DPI = 0;
    private static readonly IntPtr HWND_TOPMOST = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x; public int y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int left; public int top; public int right; public int bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [LibraryImport("user32.dll")]
    private static partial IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [LibraryImport("shcore.dll")]
    private static partial int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static partial int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static partial int GetWindowLong(IntPtr hWnd, int nIndex);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);
}
