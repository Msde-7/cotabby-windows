using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Cotabby.Core.Emoji;
using Cotabby.Core.Focus;

namespace Cotabby.App.Emoji;

/// <summary>
/// Tiny topmost, click-through panel that displays emoji matches under the
/// caret while a <c>:query</c> trigger is active. Visual peer of
/// <c>GhostOverlayWindow</c>: same WS_EX flags so it never steals focus.
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
    private static readonly IntPtr HWND_TOPMOST = new(-1);

    private HwndSource? _source;

    public EmojiPopupWindow()
    {
        InitializeComponent();
        Visibility = Visibility.Hidden;
        SourceInitialized += (_, _) =>
        {
            _source = (HwndSource)PresentationSource.FromVisual(this)!;
            int ex = GetWindowLong(_source.Handle, GWL_EXSTYLE);
            SetWindowLong(_source.Handle, GWL_EXSTYLE,
                ex | WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
        };
    }

    public void Show(IReadOnlyList<EmojiEntry> matches, string query, ScreenRect anchor, int selectedIdx)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => Show(matches, query, anchor, selectedIdx));
            return;
        }
        Items.Children.Clear();
        if (matches.Count == 0)
        {
            var hint = new TextBlock
            {
                Text = $":{query} (no match)",
                Foreground = new SolidColorBrush(Color.FromArgb(0xAA, 0xCC, 0xCC, 0xCC)),
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 12,
                Margin = new Thickness(8, 4, 8, 4),
            };
            Items.Children.Add(hint);
        }
        else
        {
            for (int i = 0; i < matches.Count; i++)
            {
                var entry = matches[i];
                var isSel = i == selectedIdx;

                var row = new Border
                {
                    Padding = new Thickness(8, 4, 12, 4),
                    CornerRadius = new CornerRadius(4),
                    Background = isSel
                        ? new SolidColorBrush(Color.FromArgb(0x55, 0x21, 0x59, 0xC8))
                        : Brushes.Transparent,
                    Margin = new Thickness(0, 1, 0, 1),
                };
                var stack = new StackPanel { Orientation = Orientation.Horizontal };
                stack.Children.Add(new TextBlock
                {
                    Text = entry.Glyph,
                    FontFamily = new FontFamily("Segoe UI Emoji"),
                    FontSize = 16,
                    Margin = new Thickness(0, 0, 8, 0),
                });
                stack.Children.Add(new TextBlock
                {
                    Text = ":" + entry.Name,
                    Foreground = new SolidColorBrush(Color.FromArgb(0xEE, 0xE0, 0xE0, 0xE0)),
                    FontFamily = new FontFamily("Segoe UI"),
                    FontSize = 12,
                    VerticalAlignment = VerticalAlignment.Center,
                });
                row.Child = stack;
                Items.Children.Add(row);
            }
        }

        Visibility = Visibility.Visible;
        if (_source is null) base.Show();
        UpdateLayout();
        PositionAt(anchor);
    }

    public new void Hide()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => Hide());
            return;
        }
        Visibility = Visibility.Hidden;
    }

    private void PositionAt(ScreenRect anchor)
    {
        if (_source is null) return;
        var matrix = _source.CompositionTarget!.TransformToDevice;
        int pxW = (int)Math.Ceiling(ActualWidth * matrix.M11);
        int pxH = (int)Math.Ceiling(ActualHeight * matrix.M22);
        // Below the caret by a couple pixels (the ghost text owns the right-of-caret
        // band; the emoji panel sits underneath so they don't fight for the same px).
        int x = (int)anchor.X;
        int y = (int)anchor.Bottom + 4;

        // Keep on-monitor: if we'd run past the right edge, hug it.
        var pt = new POINT { x = (int)anchor.X, y = (int)anchor.Y };
        var hmon = MonitorFromPoint(pt, 1);
        var mi = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        if (GetMonitorInfo(hmon, ref mi))
        {
            if (x + pxW > mi.rcWork.right) x = mi.rcWork.right - pxW - 4;
            if (x < mi.rcWork.left) x = mi.rcWork.left + 4;
            if (y + pxH > mi.rcWork.bottom)
            {
                // Flip above the caret.
                y = (int)anchor.Y - pxH - 4;
            }
        }

        SetWindowPos(_source.Handle, HWND_TOPMOST, x, y, pxW, pxH,
            SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    // ----- Win32 P/Invoke (mirrors GhostOverlayWindow.) -----

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
