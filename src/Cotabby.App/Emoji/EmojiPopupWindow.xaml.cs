using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Cotabby.Core.Emoji;
using Cotabby.Core.Focus;

namespace Cotabby.App.Emoji;

/// <summary>
/// Topmost, click-through panel that shows the emoji ribbon near the caret while a <c>:query</c>
/// capture is open. Carries the same extended window styles as <c>GhostOverlayWindow</c> so it
/// never steals focus. All members must be called on the UI thread.
/// </summary>
public partial class EmojiPopupWindow : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private static readonly IntPtr HWND_TOPMOST = new(-1);

    private static readonly Brush SelectionFill = new SolidColorBrush(Color.FromArgb(0x29, 0xFF, 0xFF, 0xFF));
    private static readonly Brush GlyphBrush = new SolidColorBrush(Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF));
    private static readonly FontFamily GlyphFont = new("Segoe UI Emoji");

    private HwndSource? _source;
    private int _selectedIndex;

    public EmojiPopupWindow()
    {
        InitializeComponent();
        QueryRow.Height = EmojiPickerMetrics.QueryRowHeight;
        QueryRow.Margin = new Thickness(EmojiPickerMetrics.HorizontalInset, 0, EmojiPickerMetrics.HorizontalInset, 0);
        RibbonRow.Height = EmojiPickerMetrics.RibbonRowHeight;
        Cells.Margin = QueryRow.Margin;
        EmptyLabel.Margin = QueryRow.Margin;

        SourceInitialized += (_, _) =>
        {
            _source = (HwndSource)PresentationSource.FromVisual(this)!;
            int ex = GetWindowLong(_source.Handle, GWL_EXSTYLE);
            SetWindowLong(_source.Handle, GWL_EXSTYLE,
                ex | WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
        };
    }

    public void ShowMatches(string query, IReadOnlyList<EmojiMatch> matches, int selectedIndex, ScreenRect caretRect)
    {
        QueryText.Text = query;
        // A typed query without matches says so. A bare colon without recents keeps the row blank.
        EmptyLabel.Visibility = matches.Count == 0 && query.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        Cells.Children.Clear();
        foreach (var match in matches)
        {
            Cells.Children.Add(new Border
            {
                Width = EmojiPickerMetrics.CellSize,
                Height = EmojiPickerMetrics.CellSize,
                Margin = new Thickness(Cells.Children.Count == 0 ? 0 : EmojiPickerMetrics.CellSpacing, 0, 0, 0),
                CornerRadius = new CornerRadius(8),
                Child = new TextBlock
                {
                    Text = match.Glyph,
                    FontFamily = GlyphFont,
                    FontSize = 20,
                    Foreground = GlyphBrush,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            });
        }

        var (width, height) = EmojiPickerMetrics.ContentSize(matches.Count);
        Width = width;
        Height = height;
        _selectedIndex = 0;
        SetSelectedIndex(selectedIndex);

        Show();
        PositionAt(caretRect, width, height);
    }

    /// <summary>Moves the selection chip and scrolls the ribbon so the selected cell is centered.</summary>
    public void SetSelectedIndex(int index)
    {
        if (index < 0 || index >= Cells.Children.Count) return;

        ((Border)Cells.Children[_selectedIndex]).Background = Brushes.Transparent;
        ((Border)Cells.Children[index]).Background = SelectionFill;
        _selectedIndex = index;

        double pitch = EmojiPickerMetrics.CellSize + EmojiPickerMetrics.CellSpacing;
        double cellCenter = EmojiPickerMetrics.HorizontalInset + index * pitch + EmojiPickerMetrics.CellSize / 2;
        Ribbon.ScrollToHorizontalOffset(cellCenter - Width / 2);
    }

    private void PositionAt(ScreenRect caretRect, double width, double height)
    {
        if (_source is null) return;

        // Metrics are device independent units while caret and monitor rectangles are physical pixels.
        var toDevice = _source.CompositionTarget!.TransformToDevice;
        double pixelWidth = Math.Ceiling(width * toDevice.M11);
        double pixelHeight = Math.Ceiling(height * toDevice.M22);

        var caretCenter = new POINT
        {
            x = (int)(caretRect.X + caretRect.Width / 2),
            y = (int)(caretRect.Y + caretRect.Height / 2),
        };
        var info = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(MonitorFromPoint(caretCenter, MONITOR_DEFAULTTONEAREST), ref info)) return;

        var work = info.rcWork;
        var frame = EmojiPickerPanelLayout.Frame(
            caretRect,
            pixelWidth,
            pixelHeight,
            new ScreenRect(work.left, work.top, work.right - work.left, work.bottom - work.top));

        SetWindowPos(_source.Handle, HWND_TOPMOST, (int)frame.X, (int)frame.Y, (int)frame.Width, (int)frame.Height,
            SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x; public int y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int left, top, right, bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public uint cbSize;
        public RECT rcMonitor, rcWork;
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
        IntPtr hWnd, IntPtr hWndInsertAfter,
        int X, int Y, int cx, int cy, uint uFlags);
}
