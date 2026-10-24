namespace Cotabby.Core.Metrics;

/// <summary>
/// One CPU, memory and GPU reading on the Performance pane's rolling timeline.
/// <see cref="Id"/> counts up from zero in each sampling session.
/// </summary>
public sealed record SystemMetricSample(
    long Id,
    DateTimeOffset Timestamp,
    double CpuPercent,
    long MemoryBytes,
    double? GpuPercent,
    double? DeviceGpuPercent,
    long? GpuMemoryBytes);

/// <summary>
/// What a sampler reports on one tick. The GPU fields are null when the counters are
/// missing or have nothing to measure from yet, so the graph shows a gap rather than 0%.
/// </summary>
/// <param name="CpuPercent">The app's CPU use since the previous reading. Can exceed 100 across cores.</param>
/// <param name="MemoryBytes">The app's private memory.</param>
/// <param name="GpuPercent">The app's own share of the GPU, 0 to 100.</param>
/// <param name="DeviceGpuPercent">GPU use across every app, 0 to 100.</param>
/// <param name="GpuMemoryBytes">GPU memory in use across every app.</param>
public sealed record SystemResourceReading(
    double CpuPercent,
    long MemoryBytes,
    double? GpuPercent = null,
    double? DeviceGpuPercent = null,
    long? GpuMemoryBytes = null);

/// <summary>Reads the app's resource use. One instance serves one sampling session.</summary>
public interface ISystemResourceSampler : IDisposable
{
    SystemResourceReading Read();
}
