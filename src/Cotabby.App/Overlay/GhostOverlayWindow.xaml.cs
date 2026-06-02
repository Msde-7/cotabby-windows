using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Cotabby.Core.Focus;
using Cotabby.Core.Overlay;

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
/// We also flip on <c>WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_NOACTIVATE |
/// WS_EX_TOOLWINDOW</c> so the window never steals focus, never appears in
/// alt-tab, and lets mouse events fall through to the host app.
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

    private HwndSource? _source;

    public GhostOverlayWindow()
    {
        InitializeComponent();
        Visibility = Visibility.Hidden;
        SourceInitialized += OnSourceInitialized;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _source = (HwndSource)PresentationSource.FromVisual(this)!;
        int ex = GetWindowLong(_source.Handle, GWL_EXSTYLE);
        SetWindowLong(_source.Handle, GWL_EXSTYLE,
            ex | WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
    }

    public void Show(string text, ScreenRect anchor)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => Show(text, anchor));
            return;
        }
        GhostText.Text = text;
        Visibility = Visibility.Visible;
        if (_source is null)
        {
            base.Show();
        }
        UpdateLayout();
        PositionAt(anchor);
    }

    public void Update(string text)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => Update(text));
            return;
        }
        GhostText.Text = text;
        UpdateLayout();
    }

    public new void Hide()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => Hide());
            return;
        }
        GhostText.Text = string.Empty;
        Visibility = Visibility.Hidden;
    }

    private void PositionAt(ScreenRect anchor)
    {
        if (_source is null) return;
        // Anchor at the right edge of the caret rect (caret rect from UIA is
        // typically a thin vertical line for text patterns). Drop the overlay
        // baseline-aligned with the caret bottom so it lives where ghost text
        // would naturally appear.
        double x = anchor.Right;
        double y = anchor.Y;

        // Win32 SetWindowPos size argument must also be in physical pixels.
        // PresentationSource.CompositionTarget gives the W -> DIP transform for
        // the monitor this HWND currently lives on; invert to get DIP -> px.
        var matrix = _source.CompositionTarget!.TransformToDevice;
        double pxW = ActualWidth * matrix.M11;
        double pxH = ActualHeight * matrix.M22;

        SetWindowPos(
            _source.Handle, HWND_TOPMOST,
            (int)x, (int)y, (int)pxW, (int)pxH,
            SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

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
}
