using System.Diagnostics;
using System.Runtime.InteropServices;
using Cotabby.Core.Metrics;

namespace Cotabby.Win32.Diagnostics;

/// <summary>
/// Reads this process's CPU and memory, and GPU figures from the same performance counters
/// Task Manager uses. CPU is processor time over wall time since the previous read. GPU comes
/// from "GPU Engine" utilization, which Windows computes between two collections, so the first
/// read of a session has no GPU share. Any counter failure reads as null, never as 0.
/// </summary>
/// <remarks>Not thread safe. Use one instance per sampling session, off the UI thread.</remarks>
public sealed partial class SystemMetricsSampler : ISystemResourceSampler
{
    private readonly Process _process = Process.GetCurrentProcess();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly nint _query;
    private readonly nint _engineCounter;
    private readonly nint _dedicatedCounter;
    private readonly nint _sharedCounter;
    private TimeSpan _lastCpu;
    private TimeSpan _lastWall;
    private bool _disposed;

    public SystemMetricsSampler()
    {
        // The first CPU reading covers the process lifetime so far.
        _process.Refresh();
        _lastCpu = TimeSpan.Zero;
        _lastWall = -(DateTime.Now - _process.StartTime);

        if (PdhOpenQuery(null, 0, out _query) != 0)
        {
            _query = 0;
            return;
        }
        // English paths work on every display language. A missing counter reads as null.
        _engineCounter = AddCounter(_query, @"\GPU Engine(*)\Utilization Percentage");
        _dedicatedCounter = AddCounter(_query, @"\GPU Adapter Memory(*)\Dedicated Usage");
        _sharedCounter = AddCounter(_query, @"\GPU Adapter Memory(*)\Shared Usage");
    }

    private static nint AddCounter(nint query, string path) =>
        PdhAddEnglishCounter(query, path, 0, out nint counter) == 0 ? counter : 0;

    public SystemResourceReading Read()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _process.Refresh();
        var cpu = _process.TotalProcessorTime;
        var wall = _clock.Elapsed;
        double elapsed = (wall - _lastWall).TotalSeconds;
        double cpuPercent = elapsed > 0 ? Math.Max((cpu - _lastCpu).TotalSeconds / elapsed * 100, 0) : 0;
        _lastCpu = cpu;
        _lastWall = wall;

        double? gpuPercent = null, devicePercent = null;
        long? gpuMemory = null;
        if (_query != 0 && PdhCollectQueryData(_query) == 0)
        {
            var engines = ReadArray(_engineCounter);
            if (engines is not null)
            {
                var readings = engines.Select(item => new GpuEngineReading(item.Name, item.Value)).ToList();
                gpuPercent = GpuEngineUsage.ProcessPercent(readings, Environment.ProcessId);
                devicePercent = GpuEngineUsage.DevicePercent(readings);
            }
            // Dedicated plus shared matches Task Manager's "GPU memory", and covers integrated GPUs,
            // whose memory is all shared.
            var dedicated = ReadArray(_dedicatedCounter);
            var shared = ReadArray(_sharedCounter);
            if (dedicated is { Count: > 0 } || shared is { Count: > 0 })
            {
                gpuMemory = (long)((dedicated ?? []).Sum(item => item.Value) + (shared ?? []).Sum(item => item.Value));
            }
        }

        return new SystemResourceReading(cpuPercent, _process.PrivateMemorySize64, gpuPercent, devicePercent, gpuMemory);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_query != 0) PdhCloseQuery(_query);
        _process.Dispose();
    }

    /// <summary>
    /// Every instance of a wildcard counter that has a valid value. Null when the counter is
    /// missing or the read fails. Instances without a value yet, such as every rate counter on
    /// the first collection, are left out.
    /// </summary>
    private static List<(string Name, double Value)>? ReadArray(nint counter)
    {
        if (counter == 0) return null;
        // Instances can appear between the size query and the read, so retry a few times.
        for (int attempt = 0; attempt < 3; attempt++)
        {
            uint size = 0;
            uint status = PdhGetFormattedCounterArray(counter, PdhFmtDouble, ref size, out _, 0);
            if (status == 0) return [];
            if (status != PdhMoreData) return null;

            nint buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                status = PdhGetFormattedCounterArray(counter, PdhFmtDouble, ref size, out uint count, buffer);
                if (status == PdhMoreData) continue;
                if (status != 0) return null;

                var items = new List<(string, double)>((int)count);
                int stride = Marshal.SizeOf<PdhFmtCounterValueItem>();
                for (int i = 0; i < count; i++)
                {
                    var item = Marshal.PtrToStructure<PdhFmtCounterValueItem>(buffer + i * stride);
                    if (item.CStatus is PdhCStatusValidData or PdhCStatusNewData)
                    {
                        items.Add((Marshal.PtrToStringUni(item.Name) ?? "", item.Value));
                    }
                }
                return items;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        return null;
    }

    private const uint PdhFmtDouble = 0x00000200;
    private const uint PdhMoreData = 0x800007D2;
    private const uint PdhCStatusValidData = 0;
    private const uint PdhCStatusNewData = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct PdhFmtCounterValueItem
    {
        public nint Name;
        public uint CStatus;
        public double Value;
    }

    [LibraryImport("pdh.dll", EntryPoint = "PdhOpenQueryW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint PdhOpenQuery(string? dataSource, nint userData, out nint query);

    [LibraryImport("pdh.dll", EntryPoint = "PdhAddEnglishCounterW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint PdhAddEnglishCounter(nint query, string path, nint userData, out nint counter);

    [LibraryImport("pdh.dll")]
    private static partial uint PdhCollectQueryData(nint query);

    [LibraryImport("pdh.dll", EntryPoint = "PdhGetFormattedCounterArrayW")]
    private static partial uint PdhGetFormattedCounterArray(
        nint counter, uint format, ref uint bufferSize, out uint itemCount, nint itemBuffer);

    [LibraryImport("pdh.dll")]
    private static partial uint PdhCloseQuery(nint query);
}
