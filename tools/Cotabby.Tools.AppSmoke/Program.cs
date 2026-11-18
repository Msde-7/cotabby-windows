// End-to-end automated smoke test for the running Cotabby app.
//
// Assumes Cotabby.exe is already launched (model loaded) and logging into the
// path passed as argv[0] (defaults to %LOCALAPPDATA%\Cotabby\logs\cotabby.log).
//
// Steps.
//   1. Open Notepad, focus it.
//   2. Synthesize typing via SendInput. Cotabby's hook ignores only the keys it sends
//      itself, so injected typing reaches it like any other input.
//   3. Wait for the model to emit (~3s).
//   4. Enumerate visible windows owned by Cotabby PID and report which ones
//      are visible (the overlay HWND should appear here).
//   5. Tail the runtime log for "Suggestion shown" lines.
//
// Exit code 0 = a suggestion was shown, non-zero = failure.

using System.Diagnostics;
using System.Runtime.InteropServices;

string logPath = args.Length > 0
    ? args[0]
    : Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Cotabby", "logs", "cotabby.log");
Console.WriteLine($"[smoke] Using runtime log: {logPath}");

if (Win32.FindCotabbyPid() == 0)
{
    Console.Error.WriteLine("[smoke] Cotabby.exe is not running. Launch it first.");
    return 2;
}
Console.WriteLine("[smoke] Cotabby.exe is running.");

// A fresh file keeps text that Notepad restores from earlier sessions out of the run.
string scratchFile = Path.Combine(Path.GetTempPath(), $"cotabby-smoke-{Guid.NewGuid():N}.txt");
File.WriteAllText(scratchFile, "");
long logStart = File.Exists(logPath) ? new FileInfo(logPath).Length : 0;
Process.Start(new ProcessStartInfo("notepad.exe", $"\"{scratchFile}\"") { UseShellExecute = true });
Console.WriteLine("[smoke] Launched notepad.exe (may be Win11 stub → Store app, PID may not match).");

// Windows 11 Notepad is a Store app and the launched PID is often a stub, so wait for a
// Notepad window showing the scratch file to appear. The name is matched without its
// extension, which a title may leave out, and the GUID in it keeps it unique.
IntPtr hwnd = IntPtr.Zero;
for (int i = 0; i < 30 && hwnd == IntPtr.Zero; i++)
{
    await Task.Delay(500);
    hwnd = Win32.FindNotepadWindow(Path.GetFileNameWithoutExtension(scratchFile));
}
if (hwnd == IntPtr.Zero)
{
    Console.Error.WriteLine("[smoke] Could not find the Notepad window with the scratch file after 15s.");
    return 3;
}
Console.WriteLine($"[smoke] Notepad HWND = 0x{(long)hwnd:X}.");
Win32.ShowWindow(hwnd, Win32.SW_RESTORE);
Win32.SetForegroundWindow(hwnd);
await Task.Delay(800);

// Windows may refuse to hand over the foreground while someone is using another app.
// Synthetic keys go to whatever has focus, so nothing is typed unless Notepad really has it.
if (Win32.GetForegroundWindow() != hwnd)
{
    Console.Error.WriteLine("[smoke] Notepad did not get the foreground. Nothing was typed. Run this while the PC is idle.");
    return 4;
}

const string text = "Thanks for the update. I will send the report";
Console.WriteLine($"[smoke] Typing {text.Length} chars into Notepad…");
foreach (char c in text)
{
    if (Win32.GetForegroundWindow() != hwnd)
    {
        Console.Error.WriteLine("[smoke] Focus left Notepad. Stopped typing.");
        return 4;
    }
    Win32.SendUnicodeChar(c);
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

// The overlay is the pass signal. The log line is written when it is shown.
bool shown = false;
if (File.Exists(logPath))
{
    using var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
    fs.Seek(logStart, SeekOrigin.Begin);
    using var sr = new StreamReader(fs);
    foreach (var line in sr.ReadToEnd().Split('\n'))
    {
        if (!line.Contains("Suggestion") && !line.Contains("Request") && !line.Contains("Accepted")) continue;
        Console.WriteLine("    " + line.TrimEnd());
        shown |= line.Contains("Suggestion shown");
    }
}

int exitCode = shown ? 0 : 1;
Console.WriteLine($"[smoke] DONE, exit={exitCode}");
return exitCode;

static partial class Win32
{
    public const int SW_RESTORE = 9;

    public static int FindCotabbyPid() => Process.GetProcessesByName("Cotabby").FirstOrDefault()?.Id ?? 0;

    /// <summary>
    /// The visible window of a notepad.exe process whose title names the file, so typing never
    /// goes into another Notepad document or an app that only has "Notepad" in its title, such
    /// as Notepad++ or a browser tab. Both the classic and the Store Notepad run as notepad.exe
    /// and put the file name in the title.
    /// </summary>
    public static IntPtr FindNotepadWindow(string fileName)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;
            var titleBuf = new System.Text.StringBuilder(512);
            GetWindowText(hwnd, titleBuf, titleBuf.Capacity);
            if (!titleBuf.ToString().Contains(fileName, StringComparison.OrdinalIgnoreCase)) return true;
            GetWindowThreadProcessId(hwnd, out int pid);
            if (!IsNotepad(pid)) return true;
            found = hwnd;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    private static bool IsNotepad(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.ProcessName.Equals("notepad", StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            // The process has exited.
            return false;
        }
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
    public static partial IntPtr GetForegroundWindow();

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

    // SendInput rejects any size but the full union's, and the mouse member is the largest.
    [StructLayout(LayoutKind.Explicit)]
    public struct INPUTUNION { [FieldOffset(0)] public KEYBDINPUT ki; [FieldOffset(0)] public MOUSEINPUT mi; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT { public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
}
