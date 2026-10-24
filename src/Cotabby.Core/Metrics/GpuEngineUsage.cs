using System.Globalization;

namespace Cotabby.Core.Metrics;

/// <summary>One "GPU Engine" counter instance and its utilization percentage.</summary>
public readonly record struct GpuEngineReading(string Instance, double Percent);

/// <summary>
/// Turns Windows "GPU Engine" utilization counters into the figures Task Manager shows.
/// Instances are named like <c>pid_1234_luid_0x0_0x187f8_phys_0_eng_0_engtype_3D</c>: one per
/// process, adapter and engine. Utilization is summed per adapter and engine type, and the
/// busiest of those groups is the GPU figure, so a busy copy engine and an idle 3D engine
/// read as busy rather than averaging out.
/// </summary>
public static class GpuEngineUsage
{
    /// <summary>The parts of an instance name the aggregation needs.</summary>
    public readonly record struct EngineInstance(int ProcessId, string Adapter, string EngineType);

    /// <summary>Parses an instance name. False for names that do not follow the pattern.</summary>
    public static bool TryParse(string instance, out EngineInstance engine)
    {
        engine = default;
        const string PidPrefix = "pid_";
        if (!instance.StartsWith(PidPrefix, StringComparison.Ordinal)) return false;

        int pidEnd = instance.IndexOf('_', PidPrefix.Length);
        int luid = instance.IndexOf("_luid_", StringComparison.Ordinal);
        int eng = instance.IndexOf("_eng_", StringComparison.Ordinal);
        int type = instance.LastIndexOf("_engtype_", StringComparison.Ordinal);
        if (pidEnd < 0 || luid != pidEnd || eng < luid || type < eng) return false;
        if (!int.TryParse(instance.AsSpan(PidPrefix.Length, pidEnd - PidPrefix.Length),
                NumberStyles.None, CultureInfo.InvariantCulture, out int pid))
        {
            return false;
        }

        string engineType = instance[(type + "_engtype_".Length)..];
        if (engineType.Length == 0) return false;
        // "luid_..._phys_N" names the adapter. Engine types compare without case, as drivers vary.
        engine = new EngineInstance(pid, instance[(luid + 1)..eng], engineType.ToLowerInvariant());
        return true;
    }

    /// <summary>
    /// The share of the GPU used by <paramref name="processId"/>, 0 to 100. Null when the process
    /// has no engine instances, so an app that never opened the GPU reads as "no data", not 0%.
    /// </summary>
    public static double? ProcessPercent(IEnumerable<GpuEngineReading> readings, int processId) =>
        Busiest(readings, engine => engine.ProcessId == processId);

    /// <summary>GPU use across every process, 0 to 100. Null when there are no readings.</summary>
    public static double? DevicePercent(IEnumerable<GpuEngineReading> readings) =>
        Busiest(readings, _ => true);

    private static double? Busiest(IEnumerable<GpuEngineReading> readings, Func<EngineInstance, bool> include)
    {
        var groups = new Dictionary<(string Adapter, string EngineType), double>();
        foreach (var reading in readings)
        {
            if (!TryParse(reading.Instance, out var engine) || !include(engine)) continue;
            if (double.IsNaN(reading.Percent)) continue;
            var key = (engine.Adapter, engine.EngineType);
            groups[key] = groups.GetValueOrDefault(key) + Math.Max(reading.Percent, 0);
        }
        return groups.Count == 0 ? null : Math.Min(groups.Values.Max(), 100);
    }
}
