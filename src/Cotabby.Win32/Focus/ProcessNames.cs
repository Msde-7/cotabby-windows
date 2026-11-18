using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Cotabby.Win32.Focus;

/// <summary>A process's executable name ("chrome") and the name its publisher gives it ("Google Chrome").</summary>
internal readonly record struct ProcessNameSet(string Process, string Display)
{
    public static ProcessNameSet None { get; } = new("", "");
}

/// <summary>
/// The names of a process, read once per process. The display name comes from the executable's
/// file version info. The prompt names the app the way people do ("Google Chrome", not "chrome"),
/// which also lets a window title's " - Google Chrome" suffix be recognized and left out.
/// </summary>
/// <remarks>
/// Every focus read needs the names, and looking up a process by id walks every running process,
/// about 60 microseconds with 540 of them. Process ids are reused, so a cached entry counts only
/// for the process that started at the same time, which costs one handle open, about 11 microseconds.
/// </remarks>
internal static partial class ProcessNames
{
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const int CacheLimit = 256;

    private sealed record Entry(long StartedAt, ProcessNameSet Names);

    private static readonly ConcurrentDictionary<int, Entry> Cache = new();

    public static ProcessNameSet For(int pid)
    {
        if (pid <= 0) return ProcessNameSet.None;
        IntPtr process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        try
        {
            // Zero when the process cannot be opened. Its names are then read every time, as before.
            long startedAt = process != IntPtr.Zero && GetProcessTimes(process, out long creation, out _, out _, out _)
                ? creation
                : 0;
            if (startedAt != 0 && Cache.TryGetValue(pid, out var cached) && cached.StartedAt == startedAt)
            {
                return cached.Names;
            }
            var names = new ProcessNameSet(ProcessName(pid), process == IntPtr.Zero ? "" : DisplayName(process));
            if (startedAt != 0)
            {
                // Exited processes leave entries behind, so the cache is emptied rather than left to grow.
                if (Cache.Count >= CacheLimit) Cache.Clear();
                Cache[pid] = new Entry(startedAt, names);
            }
            return names;
        }
        finally
        {
            if (process != IntPtr.Zero) CloseHandle(process);
        }
    }

    private static string ProcessName(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.ProcessName;
        }
        catch (Exception) { return string.Empty; }
    }

    private static unsafe string DisplayName(IntPtr process)
    {
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
        catch (Exception)
        {
            return "";
        }
    }

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessTimes(
        IntPtr process, out long creation, out long exit, out long kernel, out long user);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool QueryFullProcessImageName(IntPtr process, uint flags, char* name, ref uint size);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);
}
