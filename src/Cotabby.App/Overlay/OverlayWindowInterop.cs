using System.Runtime.InteropServices;
using System.Windows;
using Cotabby.Core.Focus;

namespace Cotabby.App.Overlay;

/// <summary>
/// Win32 calls shared by the windows drawn over other apps. They are placed in the physical pixels
/// UI Automation reports, since WPF places windows at the primary monitor's scale.
/// </summary>
internal static partial class OverlayWindowInterop
{
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

    /// <summary>Keeps the window out of the taskbar, focus and mouse input.</summary>
    public static void MakeClickThrough(IntPtr hwnd) =>
        SetWindowLong(hwnd, GWL_EXSTYLE,
            GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);

    public static void PlaceTopmost(IntPtr hwnd, double x, double y, double width, double height) =>
        SetWindowPos(hwnd, HWND_TOPMOST, (int)x, (int)y, (int)width, (int)height, SWP_NOACTIVATE | SWP_SHOWWINDOW);

    /// <summary>Physical pixels per DIP on the monitor nearest the point.</summary>
    public static double DipScaleAt(double x, double y) =>
        GetDpiForMonitor(MonitorAt(x, y), MDT_EFFECTIVE_DPI, out uint dpiX, out _) == 0 && dpiX > 0 ? dpiX / 96.0 : 1.0;

    /// <summary>The work area of the monitor nearest the point, or the primary screen when unknown.</summary>
    public static ScreenRect WorkAreaAt(double x, double y)
    {
        var info = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(MonitorAt(x, y), ref info))
        {
            return new ScreenRect(0, 0, SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight);
        }
        var work = info.rcWork;
        return new ScreenRect(work.left, work.top, work.right - work.left, work.bottom - work.top);
    }

    private static IntPtr MonitorAt(double x, double y) =>
        MonitorFromPoint(new POINT { x = (int)x, y = (int)y }, MONITOR_DEFAULTTONEAREST);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x, y; }

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

    [LibraryImport("shcore.dll")]
    private static partial int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static partial int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static partial int GetWindowLong(IntPtr hWnd, int nIndex);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);
}
