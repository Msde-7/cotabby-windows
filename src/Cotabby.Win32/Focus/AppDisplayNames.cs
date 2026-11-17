using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Cotabby.Win32.Focus;

/// <summary>
/// The name an app's publisher gives it, read once per process from its file version info. The
/// prompt names the app the way people do ("Google Chrome", not "chrome"), which also lets a window
/// title's " - Google Chrome" suffix be recognized and left out.
/// </summary>
internal static partial class AppDisplayNames
{
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private static readonly ConcurrentDictionary<int, string> Cache = new();

    public static string For(int pid)
    {
        if (pid <= 0) return "";
        if (Cache.TryGetValue(pid, out var cached)) return cached;
        string name = Read(pid);
        // Process ids are reused, so the cache stays small rather than growing for the session.
        if (Cache.Count > 256) Cache.Clear();
        Cache[pid] = name;
        return name;
    }

    private static unsafe string Read(int pid)
    {
        try
        {
            IntPtr process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
            if (process == IntPtr.Zero) return "";
            try
            {
                char* buffer = stackalloc char[1024];
                uint size = 1024;
                if (!QueryFullProcessImageName(process, 0, buffer, ref size)) return "";
                var info = FileVersionInfo.GetVersionInfo(new string(buffer, 0, (int)size));
                string name = (info.FileDescription ?? "").Trim();
                if (name.Length == 0) name = (info.ProductName ?? "").Trim();
                return name;
            }
            finally
            {
                CloseHandle(process);
            }
        }
        catch (Exception)
        {
            return "";
        }
    }

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool QueryFullProcessImageName(IntPtr process, uint flags, char* name, ref uint size);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);
}
