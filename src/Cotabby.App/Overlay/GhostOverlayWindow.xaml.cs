using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Cotabby.App.Settings;
using Cotabby.App.UI;
using Cotabby.Core.Focus;
using Cotabby.Core.Overlay;
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
    private const double MinFontSize = 11;
    private const double MaxFontSize = 48;
    private const string CorrectionPrefix = "→ ";
    private static readonly Color CorrectionColor = Color.FromRgb(0x22, 0xC5, 0x5E);

    private HwndSource? _source;
    private Color? _userColor;
    private double _opacity = 1.0;
    private double _sizeMultiplier = 1.0;
    private bool _showHint = true;
    private Duration _fadeIn = Duration.Automatic;
    private bool _isCorrection;

    // UIA flips between the real line height and a coarser fallback while a suggestion
    // is visible. Holding the larger reading keeps the ghost from shrinking mid-suggestion.
    private double _stableCaretHeightPx;

    public GhostOverlayWindow()
    {
        InitializeComponent();
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
        _showHint = settings.ShowAcceptanceHint;
        TabKeycapLabel.Text = settings.AcceptWord.IsBound ? settings.AcceptWord.Label : "";
        _fadeIn = settings.FadeInSuggestions && settings.FadeInSeconds > 0
            ? new Duration(TimeSpan.FromSeconds(settings.FadeInSeconds))
            : Duration.Automatic;
        ApplyPalette();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _source = (HwndSource)PresentationSource.FromVisual(this)!;
        int style = GetWindowLong(_source.Handle, GWL_EXSTYLE);
        SetWindowLong(_source.Handle, GWL_EXSTYLE,
            style | WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
    }

    public void Show(string text, ScreenRect anchor, bool isCorrection = false)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => Show(text, anchor, isCorrection));
            return;
        }

        bool wasVisible = Visibility == Visibility.Visible;
        ApplyFontSize(anchor);
        if (_isCorrection != isCorrection)
        {
            _isCorrection = isCorrection;
            ApplyPalette();
        }
        SetText(isCorrection ? CorrectionPrefix + text : text);
        Visibility = Visibility.Visible;
        UpdateLayout();
        PositionAt(anchor);
        if (!wasVisible && _fadeIn.HasTimeSpan)
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
        GhostText.Text = text;
        TabKeycap.Visibility = _showHint && text.Length > 0 && TabKeycapLabel.Text.Length > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void ApplyFontSize(ScreenRect anchor)
    {
        if (anchor.Height <= 1) return;
        _stableCaretHeightPx = Math.Max(_stableCaretHeightPx, anchor.Height);

        double scale = _source?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;
        double caretHeightDip = _stableCaretHeightPx / Math.Max(scale, 0.1);
        GhostText.FontSize = Math.Round(
            Math.Clamp(caretHeightDip * FontSizePerCaretHeight * _sizeMultiplier, MinFontSize, MaxFontSize), 1);
    }

    private void ApplyPalette()
    {
        bool dark = SystemTheme.IsDark;
        var text = _isCorrection ? CorrectionColor : _userColor ?? (dark ? Color.FromRgb(0xA6, 0xA6, 0xA6) : Color.FromRgb(0x73, 0x73, 0x73));
        GhostText.Foreground = new SolidColorBrush(Color.FromArgb((byte)Math.Round(_opacity * 0xCC), text.R, text.G, text.B));

        TabKeycap.Background = Brush(dark ? 0x2E : 0xF2);
        TabKeycap.BorderBrush = Brush(dark ? 0x4D : 0xCC);
        TabKeycapLabel.Foreground = Brush(dark ? 0xA6 : 0x73);

        static SolidColorBrush Brush(int gray) => new(Color.FromRgb((byte)gray, (byte)gray, (byte)gray));
    }

    private void PositionAt(ScreenRect anchor)
    {
        if (_source?.CompositionTarget is not { } target) return;

        double width = ActualWidth * target.TransformToDevice.M11;
        double height = ActualHeight * target.TransformToDevice.M22;
        double x = anchor.Right + 1;
        double y = anchor.Y + Math.Max(0, (anchor.Height - height) / 2.0);

        // Keep the whole overlay inside the work area of the monitor under the caret.
        var area = WorkAreaAt(anchor);
        if (x + width > area.right) x = area.right - width - 2;
        if (x < area.left) x = area.left + 2;
        if (y + height > area.bottom) y = anchor.Y - height - 2;
        if (y < area.top) y = area.top + 2;

        SetWindowPos(_source.Handle, HWND_TOPMOST, (int)x, (int)y, (int)width, (int)height,
            SWP_NOACTIVATE | SWP_SHOWWINDOW);
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

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static partial int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static partial int GetWindowLong(IntPtr hWnd, int nIndex);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);
}
