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

    /// <summary>System accent stand-in — the macOS picker highlights the
    /// selected row with the OS accent; we hardcode the same blue the rest of
    /// the port uses.</summary>
    private static readonly Color Accent = Color.FromRgb(0x00, 0x7A, 0xFF);

    public void Show(IReadOnlyList<EmojiEntry> matches, string query, ScreenRect anchor, int selectedIdx)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => Show(matches, query, anchor, selectedIdx));
            return;
        }
        bool dark = IsSystemInDarkMode();
        Panel.Background = new SolidColorBrush(dark
            ? Color.FromArgb(0xF5, 0x1F, 0x1F, 0x1F)
            : Color.FromArgb(0xF7, 0xFA, 0xFA, 0xFA));
        Panel.BorderBrush = new SolidColorBrush(dark
            ? Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)
            : Color.FromArgb(0x1A, 0x00, 0x00, 0x00));
        HeaderDivider.Background = Panel.BorderBrush;
        var primary = dark ? Color.FromRgb(0xE8, 0xE8, 0xE8) : Color.FromRgb(0x1D, 0x1D, 0x1F);
        var secondary = dark ? Color.FromRgb(0x9A, 0x9A, 0x9A) : Color.FromRgb(0x6E, 0x6E, 0x73);
        HeaderColon.Foreground = new SolidColorBrush(secondary);
        HeaderQuery.Foreground = new SolidColorBrush(primary);
        HeaderQuery.Text = query;

        Items.Children.Clear();
        if (matches.Count == 0)
        {
            Items.Children.Add(new TextBlock
            {
                Text = "No emoji found",
                Foreground = new SolidColorBrush(secondary),
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 12,
                Margin = new Thickness(8, 6, 8, 6),
            });
        }
        else
        {
            for (int i = 0; i < matches.Count; i++)
            {
                var entry = matches[i];
                var isSel = i == selectedIdx;

                var row = new Border
                {
                    Height = 30,
                    Padding = new Thickness(8, 0, 10, 0),
                    CornerRadius = new CornerRadius(6),
                    Background = isSel ? new SolidColorBrush(Accent) : Brushes.Transparent,
                };
                var stack = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                stack.Children.Add(new TextBlock
                {
                    Text = entry.Glyph,
                    FontFamily = new FontFamily("Segoe UI Emoji"),
                    FontSize = 18,
                    Margin = new Thickness(0, 0, 8, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                });
                stack.Children.Add(new TextBlock
                {
                    Text = ":" + entry.Name + ":",
                    Foreground = new SolidColorBrush(isSel ? Colors.White : primary),
                    FontFamily = new FontFamily("Segoe UI"),
                    FontSize = 13,
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

    private static bool IsSystemInDarkMode()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch (Exception) { return false; }
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
