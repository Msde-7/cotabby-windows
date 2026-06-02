// End-to-end automated smoke test for the running Cotabby app.
//
// Assumes Cotabby.exe is already launched (model loaded) and logging into the
// path passed as argv[0] (defaults to C:\tmp\cotabby-live.log).
//
// Test:
//   1. Open Notepad, focus it.
//   2. Synthesize typing via SendInput. Cotabby's hook drops LLKHF_INJECTED
//      events, so this proves Notepad-side input handling but not the hook
//      itself — the hook is exercised by the unit tests.
//   3. Wait for the model to emit (~3s).
//   4. Enumerate visible windows owned by Cotabby PID and report which ones
//      are visible (the overlay HWND should appear here).
//   5. Tail the runtime log for "Overlay shown" lines.
//
// Exit code 0 = overlay window found, non-zero = failure.

using System.Diagnostics;
using System.Runtime.InteropServices;

string logPath = args.Length > 0 ? args[0] : @"C:\tmp\cotabby-live.log";
Console.WriteLine($"[smoke] Using runtime log: {logPath}");

if (!Win32.IsCotabbyRunning())
{
    Console.Error.WriteLine("[smoke] Cotabby.exe is not running. Launch it first.");
    return 2;
}
Console.WriteLine("[smoke] Cotabby.exe is running.");

Process.Start(new ProcessStartInfo("notepad.exe") { UseShellExecute = true });
Console.WriteLine("[smoke] Launched notepad.exe (may be Win11 stub → Store app, PID may not match).");

// Win11 Notepad has both a legacy and a Store version; the launched PID is
// often a stub. Wait for a Notepad window to appear in the foreground.
IntPtr hwnd = IntPtr.Zero;
for (int i = 0; i < 30 && hwnd == IntPtr.Zero; i++)
{
    await Task.Delay(500);
    hwnd = Win32.FindNotepadWindow();
}
if (hwnd == IntPtr.Zero)
{
    Console.Error.WriteLine("[smoke] Could not find a Notepad top-level window after 15s.");
    return 3;
}
Console.WriteLine($"[smoke] Notepad HWND = 0x{(long)hwnd:X}.");
Win32.ShowWindow(hwnd, Win32.SW_RESTORE);
Win32.SetForegroundWindow(hwnd);
await Task.Delay(800);

const string text = "def fibonacci(n):\n    if n < 2:\n        return n\n    return ";
Console.WriteLine($"[smoke] Typing {text.Length} chars into Notepad…");
foreach (char c in text)
{
    if (c == '\n')
    {
        Win32.SendVk(0x0D); // Enter
    }
    else
    {
        Win32.SendUnicodeChar(c);
    }
    await Task.Delay(15);
}

Console.WriteLine("[smoke] Waiting 4s for suggestion to render…");
await Task.Delay(4000);

var cotabbyPid = Win32.FindCotabbyPid();
Console.WriteLine($"[smoke] Cotabby PID = {cotabbyPid}.");
var overlays = Win32.FindVisibleTopmostWindows(cotabbyPid);
Console.WriteLine($"[smoke] Found {overlays.Count} visible Cotabby HWND(s):");
foreach (var w in overlays)
{
    Console.WriteLine($"           hwnd=0x{(long)w.Handle:X} cls='{w.ClassName}' rect={w.Rect}");
}

Console.WriteLine();
Console.WriteLine("[smoke] Tail of runtime log (filtered):");
if (File.Exists(logPath))
{
    using var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
    using var sr = new StreamReader(fs);
    var lines = sr.ReadToEnd().Split('\n').Reverse().Take(80).Reverse().ToList();
    foreach (var line in lines)
    {
        if (line.Contains("Overlay shown") || line.Contains("Triggering") ||
            line.Contains("Cancel") || line.Contains("not ready") ||
            line.Contains("host=Notepad"))
        {
            Console.WriteLine("    " + line.TrimEnd());
        }
    }
}
else
{
    Console.WriteLine("    (log file not found)");
}

int exitCode = overlays.Any() ? 0 : 1;
Console.WriteLine();
Console.WriteLine($"[smoke] DONE — exit={exitCode}");
return exitCode;

static partial class Win32
{
    public const int SW_RESTORE = 9;

    public static bool IsCotabbyRunning() =>
        Process.GetProcessesByName("Cotabby").Length > 0;

    public static int FindCotabbyPid()
    {
        var p = Process.GetProcessesByName("Cotabby").FirstOrDefault();
        return p?.Id ?? 0;
    }

    public static IntPtr FindNotepadWindow()
    {
        // Match by window-title (most reliable; works for both classic and
        // Win11 Store Notepad) OR by class name "Notepad" (classic only).
        IntPtr found = IntPtr.Zero;
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;
            var clsBuf = new System.Text.StringBuilder(256);
            GetClassName(hwnd, clsBuf, clsBuf.Capacity);
            var cls = clsBuf.ToString();
            var titleBuf = new System.Text.StringBuilder(512);
            GetWindowText(hwnd, titleBuf, titleBuf.Capacity);
            var title = titleBuf.ToString();
            if (cls == "Notepad" || title.Contains("Notepad", StringComparison.OrdinalIgnoreCase))
            {
                found = hwnd;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetWindowTextW")]
    public static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);

    public record WindowInfo(IntPtr Handle, string ClassName, RECT Rect);

    public static List<WindowInfo> FindVisibleTopmostWindows(int pid)
    {
        var results = new List<WindowInfo>();
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;
            GetWindowThreadProcessId(hwnd, out int p);
            if (p != pid) return true;
            var sb = new System.Text.StringBuilder(256);
            GetClassName(hwnd, sb, sb.Capacity);
            GetWindowRect(hwnd, out RECT r);
            if (r.Right - r.Left <= 0 || r.Bottom - r.Top <= 0) return true;
            results.Add(new WindowInfo(hwnd, sb.ToString(), r));
            return true;
        }, IntPtr.Zero);
        return results;
    }

    public static void SendUnicodeChar(char c)
    {
        var inputs = new INPUT[]
        {
            new() { type = 1, U = new INPUTUNION { ki = new KEYBDINPUT { wVk = 0, wScan = c, dwFlags = 0x4, time = 0, dwExtraInfo = IntPtr.Zero } } },
            new() { type = 1, U = new INPUTUNION { ki = new KEYBDINPUT { wVk = 0, wScan = c, dwFlags = 0x4 | 0x2, time = 0, dwExtraInfo = IntPtr.Zero } } },
        };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    public static void SendVk(ushort vk)
    {
        var inputs = new INPUT[]
        {
            new() { type = 1, U = new INPUTUNION { ki = new KEYBDINPUT { wVk = vk, wScan = 0, dwFlags = 0, time = 0, dwExtraInfo = IntPtr.Zero } } },
            new() { type = 1, U = new INPUTUNION { ki = new KEYBDINPUT { wVk = vk, wScan = 0, dwFlags = 0x2, time = 0, dwExtraInfo = IntPtr.Zero } } },
        };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindowVisible(IntPtr hWnd);

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial uint GetWindowThreadProcessId(IntPtr hWnd, out int lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetClassNameW")]
    public static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetForegroundWindow(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
        public override string ToString() => $"({Left},{Top})-({Right},{Bottom}) {Right - Left}x{Bottom - Top}";
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT { public uint type; public INPUTUNION U; }

    [StructLayout(LayoutKind.Explicit)]
    public struct INPUTUNION { [FieldOffset(0)] public KEYBDINPUT ki; }

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
}
