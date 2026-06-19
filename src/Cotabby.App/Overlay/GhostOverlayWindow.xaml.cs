using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Cotabby.Core.Focus;
using Cotabby.Core.Overlay;
using Microsoft.Win32;

namespace Cotabby.App.Overlay;

/// <summary>
/// Transparent, topmost, click-through window that renders ghost text near the
/// caret. Implements <see cref="IOverlayPresenter"/> so the coordinator can
/// drive it without taking a WPF dependency.
/// </summary>
/// <remarks>
/// The window participates in WPF compositing but bypasses WPF's positioning
/// system: WPF <c>Left</c>/<c>Top</c> are DIPs at primary-monitor scale, which
/// is wrong on multi-monitor setups with mixed DPI. After
/// <c>SourceInitialized</c> we drive position via <c>SetWindowPos</c> with the
/// raw physical pixels we already get from UIA, then convert back to DIPs for
/// the <c>Width</c>/<c>Height</c> on the target monitor.
///
/// Visual style mirrors the macOS port's <c>GhostSuggestionView</c>: no panel
/// background, foreground color tracks system light/dark, font size derives
/// from the caret height so ghost text matches the host editor's line height.
/// </remarks>
public partial class GhostOverlayWindow : Window, IOverlayPresenter
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private static readonly IntPtr HWND_TOPMOST = new(-1);

    // Per-session floor for caret-derived font size. UIA's caret-rect height
    // flickers between the real line height and a coarse field-height fallback;
    // a per-session floor keeps ghost text from shrinking mid-completion when
    // the trustworthy reading happens to be the smaller of the two.
    private double _stabilizedCaretHeightPx;

    private HwndSource? _source;

    // User-selected appearance overrides. Null = use auto/system palette.
    // Set via ApplyAppearance from the settings UI; survive theme changes.
    private Color? _userColor;
    private double _userOpacity = 1.0;
    private bool _showHint = true;

    public GhostOverlayWindow()
    {
        InitializeComponent();
        Visibility = Visibility.Hidden;
        SourceInitialized += OnSourceInitialized;
        ApplyThemePalette();
        // Pick up OS theme switches without restart.
        SystemEvents.UserPreferenceChanged += (_, _) => Dispatcher.BeginInvoke(ApplyThemePalette);
    }

    /// <summary>
    /// Apply user appearance choices from settings. Re-evaluates the palette
    /// immediately so the next Show / a currently-visible overlay both pick up
    /// the change without restart. <paramref name="colorId"/> is a
    /// <see cref="Cotabby.App.UI.GhostTextPalette"/> id ("auto", "gray", …) or
    /// a "#RRGGBB" hex string.
    /// </summary>
    public void ApplyAppearance(string colorId, double opacity, bool showHint)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => ApplyAppearance(colorId, opacity, showHint));
            return;
        }
        _userColor = Cotabby.App.UI.GhostTextPalette.Resolve(colorId);
        _userOpacity = Math.Clamp(opacity, 0.10, 1.0);
        _showHint = showHint;
        ApplyThemePalette();
        // If overlay is currently visible, make sure the hint band reflects the
        // new toggle without waiting for the next Show.
        if (GhostText.Text.Length > 0)
        {
            TabKeycap.Visibility = _showHint ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _source = (HwndSource)PresentationSource.FromVisual(this)!;
        int ex = GetWindowLong(_source.Handle, GWL_EXSTYLE);
        SetWindowLong(_source.Handle, GWL_EXSTYLE,
            ex | WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
    }

    /// <summary>
    /// When set, the overlay ignores the caret anchor and pins itself to the
    /// top-left of the primary monitor. Used purely for "is the overlay window
    /// even rendering?" debugging — set COTABBY_DEBUG_OVERLAY=1 to enable.
    /// </summary>
    private static readonly bool DebugFixedPosition =
        Environment.GetEnvironmentVariable("COTABBY_DEBUG_OVERLAY") == "1";

    public void Show(string text, ScreenRect anchor)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => Show(text, anchor));
            return;
        }
        ApplyCaretGeometry(anchor);
        GhostText.Text = text;
        TabKeycap.Visibility = (_showHint && !string.IsNullOrEmpty(text))
            ? Visibility.Visible : Visibility.Collapsed;
        Visibility = Visibility.Visible;
        if (_source is null)
        {
            base.Show();
        }
        UpdateLayout();
        if (DebugFixedPosition)
        {
            PositionAtFixed();
        }
        else
        {
            PositionAt(anchor);
        }
    }

    private void PositionAtFixed()
    {
        if (_source is null) return;
        var matrix = _source.CompositionTarget!.TransformToDevice;
        int pxW = (int)(ActualWidth * matrix.M11);
        int pxH = (int)(ActualHeight * matrix.M22);
        var primary = GetPrimaryWorkArea();
        SetWindowPos(_source.Handle, HWND_TOPMOST,
            (int)primary.Left + 40, (int)primary.Top + 40,
            pxW, pxH, SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    public void Update(string text)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => Update(text));
            return;
        }
        GhostText.Text = text;
        TabKeycap.Visibility = (_showHint && !string.IsNullOrEmpty(text))
            ? Visibility.Visible : Visibility.Collapsed;
        UpdateLayout();
    }

    /// <summary>Currently-rendered ghost text. Test seam for the self-test harness
    /// so it can verify what the overlay is actually displaying without coupling
    /// to the XAML element tree.</summary>
    internal string CurrentGhostText => GhostText.Text;

    public new void Hide()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => Hide());
            return;
        }
        GhostText.Text = string.Empty;
        TabKeycap.Visibility = Visibility.Collapsed;
        _stabilizedCaretHeightPx = 0;
        Visibility = Visibility.Hidden;
    }

    /// <summary>
    /// Scales the ghost text + keycap to match the caret line height. UIA reports
    /// the caret rect in physical pixels; WPF font sizes are in DIPs, so we
    /// divide by the source's device pixel ratio before applying. Empty/zero
    /// caret heights (some apps return a degenerate rect on first focus) keep
    /// the previous size rather than collapsing the overlay to nothing.
    /// </summary>
    private void ApplyCaretGeometry(ScreenRect anchor)
    {
        double caretHeightPx = anchor.Height;
        if (caretHeightPx <= 1) return;

        // Keep ghost text from shrinking mid-session: UIA frequently flips
        // between the real line-height rect (~28px) and a field-height fallback
        // (~18px) between polls; stabilize on the larger.
        if (caretHeightPx < _stabilizedCaretHeightPx)
        {
            caretHeightPx = _stabilizedCaretHeightPx;
        }
        else
        {
            _stabilizedCaretHeightPx = caretHeightPx;
        }

        double scale = 1.0;
        if (_source?.CompositionTarget is { } ct)
        {
            // M22 is the y-axis device pixel ratio (DPI / 96).
            double m22 = ct.TransformToDevice.M22;
            if (m22 > 0.1) scale = m22;
        }

        double caretHeightDip = caretHeightPx / scale;
        // 0.78 matches the macOS GhostSuggestionView ratio: font_size = line_height * 0.78.
        double fontSize = Math.Clamp(caretHeightDip * 0.78, 12, 22);
        GhostText.FontSize = Math.Round(fontSize, 1);
        TabKeycapLabel.FontSize = Math.Max(9, fontSize - 4);
    }

    private void ApplyThemePalette()
    {
        bool darkMode = IsSystemInDarkMode();

        // The user can override the ghost text color from settings; if they
        // haven't, we pick a translucent gray that contrasts with the host
        // background (light gray on dark, dark gray on light).
        Color textColor = _userColor ?? (darkMode
            ? Color.FromRgb(0xA6, 0xA6, 0xA6)
            : Color.FromRgb(0x73, 0x73, 0x73));

        // Opacity is applied to alpha so the keycap chrome stays readable.
        byte alpha = (byte)Math.Round(Math.Clamp(_userOpacity, 0.10, 1.0) * 0xCC);
        GhostText.Foreground = new SolidColorBrush(Color.FromArgb(alpha,
            textColor.R, textColor.G, textColor.B));

        if (darkMode)
        {
            TabKeycap.Background = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
            TabKeycap.BorderBrush = new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF));
            TabKeycapLabel.Foreground = new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF));
        }
        else
        {
            TabKeycap.Background = new SolidColorBrush(Color.FromArgb(0x33, 0x00, 0x00, 0x00));
            TabKeycap.BorderBrush = new SolidColorBrush(Color.FromArgb(0x55, 0x00, 0x00, 0x00));
            TabKeycapLabel.Foreground = new SolidColorBrush(Color.FromArgb(0xCC, 0x55, 0x55, 0x55));
        }
    }

    private static bool IsSystemInDarkMode()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            // 0 = dark, 1 = light. Missing key on older builds defaults to light.
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch (Exception) { return false; }
    }

    private void PositionAt(ScreenRect anchor)
    {
        if (_source is null) return;

        var matrix = _source.CompositionTarget!.TransformToDevice;
        double pxW = ActualWidth * matrix.M11;
        double pxH = ActualHeight * matrix.M22;

        // Default: right of caret, baseline-aligned. The TextBlock's top-left
        // sits a few pixels above the caret's baseline, so shift down slightly
        // so the descender line meets the host caret instead of floating above.
        double x = anchor.Right + 1;
        double y = anchor.Y + Math.Max(0, (anchor.Height - pxH) / 2.0);

        // Avoid clipping against monitor edges. Pick the monitor under the
        // caret (the work area excludes the taskbar) and clamp the overlay
        // so all of it stays visible. Flip above the caret if there isn't
        // room below.
        var monitor = GetMonitorBounds(anchor);
        if (x + pxW > monitor.Right) x = monitor.Right - pxW - 2;
        if (x < monitor.Left) x = monitor.Left + 2;

        if (y + pxH > monitor.Bottom)
        {
            // Flip above the caret line.
            y = anchor.Y - pxH - 2;
        }
        if (y < monitor.Top) y = monitor.Top + 2;

        SetWindowPos(
            _source.Handle, HWND_TOPMOST,
            (int)x, (int)y, (int)pxW, (int)pxH,
            SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    private static (double Left, double Top, double Right, double Bottom) GetMonitorBounds(ScreenRect anchor)
    {
        // Use Win32 monitor-from-point so we don't pull in WindowsForms.
        var pt = new POINT { x = (int)anchor.X, y = (int)anchor.Y };
        var hmon = MonitorFromPoint(pt, MONITOR_DEFAULTTOPRIMARY);
        var mi = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        if (GetMonitorInfo(hmon, ref mi))
        {
            return (mi.rcWork.left, mi.rcWork.top, mi.rcWork.right, mi.rcWork.bottom);
        }
        return GetPrimaryWorkArea();
    }

    private static (double Left, double Top, double Right, double Bottom) GetPrimaryWorkArea()
    {
        var hmon = MonitorFromPoint(default, MONITOR_DEFAULTTOPRIMARY);
        var mi = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        if (GetMonitorInfo(hmon, ref mi))
        {
            return (mi.rcWork.left, mi.rcWork.top, mi.rcWork.right, mi.rcWork.bottom);
        }
        return (0, 0, 1920, 1080);
    }

    private const uint MONITOR_DEFAULTTOPRIMARY = 1;

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

    // Win32 ANSI/Unicode entry-point convention: the public name in user32.dll is
    // `GetMonitorInfoW` (Unicode) — there is no plain `GetMonitorInfo` symbol on
    // x64 builds of Windows even though DllImport's CharSet would normally handle
    // that. LibraryImport doesn't do the suffix auto-resolution that DllImport
    // does, so we must spell it out.
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
        IntPtr hWnd, IntPtr hWndInsertAfter,
        int X, int Y, int cx, int cy,
        uint uFlags);

    // Test helper — read the actual HWND rect for verification.
    internal static void GetWindowRectForTest(IntPtr hwnd, out int x, out int y, out int w, out int h)
    {
        x = y = w = h = 0;
        if (GetWindowRectNative(hwnd, out RECT r))
        {
            x = r.left; y = r.top;
            w = r.right - r.left;
            h = r.bottom - r.top;
        }
    }

    [LibraryImport("user32.dll", EntryPoint = "GetWindowRect")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRectNative(IntPtr hwnd, out RECT lpRect);
}
