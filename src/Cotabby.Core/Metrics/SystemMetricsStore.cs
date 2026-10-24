namespace Cotabby.Core.Metrics;

/// <summary>
/// A rolling window of the app's own CPU, memory and GPU readings for the Performance pane's
/// live graphs. Nothing is persisted and nothing runs while no one is watching: sampling is
/// reference counted through <see cref="BeginSampling"/> and <see cref="EndSampling"/>, and
/// the last viewer leaving stops the timer and drops the window.
/// </summary>
/// <remarks>
/// Each session gets a fresh sampler from the factory, so a GPU baseline never spans the time
/// the pane was closed. Readings are taken on the timer's thread, never the UI thread, and
/// <see cref="Changed"/> is raised there too.
/// </remarks>
public sealed class SystemMetricsStore(
    Func<ISystemResourceSampler> samplerFactory, TimeSpan? sampleInterval = null, TimeProvider? timeProvider = null)
    : IDisposable
{
    /// <summary>At one sample a second this is a rolling minute.</summary>
    public const int MaximumSamples = 60;

    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(1);

    private readonly TimeSpan _interval = sampleInterval ?? DefaultInterval;
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly object _gate = new();
    // Held for each read, and for disposing a sampler, so a sampler never closes mid read.
    // The UI thread never takes it.
    private readonly object _readGate = new();
    private readonly Queue<SystemMetricSample> _samples = new();
    private ITimer? _timer;
    private ISystemResourceSampler? _sampler;
    private int _activeRequests;
    private long _nextId;
    // Bumped whenever a session ends so a tick already in flight cannot append to the next one.
    private int _session;
    private int _capturing;

    public event Action? Changed;

    public IReadOnlyList<SystemMetricSample> Samples
    {
        get { lock (_gate) return _samples.ToArray(); }
    }

    /// <summary>Registers a viewer. The first one starts the timer, which reads right away.</summary>
    public void BeginSampling()
    {
        lock (_gate)
        {
            _activeRequests++;
            if (_timer is not null) return;
            int session = _session;
            _timer = _time.CreateTimer(_ => Capture(session), null, TimeSpan.Zero, _interval);
        }
    }

    /// <summary>Balances <see cref="BeginSampling"/>. The last viewer leaving stops sampling.</summary>
    public void EndSampling()
    {
        lock (_gate)
        {
            _activeRequests = Math.Max(0, _activeRequests - 1);
            if (_activeRequests > 0) return;
            StopLocked();
        }
        Changed?.Invoke();
    }

    /// <summary>Drops the window and restarts sample ids without stopping the timer.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _samples.Clear();
            _nextId = 0;
        }
        Changed?.Invoke();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _activeRequests = 0;
            StopLocked();
        }
    }

    private void StopLocked()
    {
        _timer?.Dispose();
        _timer = null;
        _session++;
        if (_sampler is { } sampler)
        {
            _sampler = null;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                lock (_readGate) sampler.Dispose();
            });
        }
        _samples.Clear();
        _nextId = 0;
    }

    private void Capture(int session)
    {
        // A slow read must not stack ticks on top of each other.
        if (Interlocked.Exchange(ref _capturing, 1) == 1) return;
        try
        {
            SystemResourceReading reading;
            lock (_readGate)
            {
                ISystemResourceSampler? sampler;
                lock (_gate)
                {
                    if (session != _session) return;
                    sampler = _sampler;
                }
                if (sampler is null)
                {
                    // Opening a sampler can take a few milliseconds, so it happens outside the gate.
                    sampler = samplerFactory();
                    lock (_gate)
                    {
                        if (session != _session)
                        {
                            sampler.Dispose();
                            return;
                        }
                        _sampler = sampler;
                    }
                }
                reading = sampler.Read();
            }

            lock (_gate)
            {
                if (session != _session) return;
                _samples.Enqueue(new SystemMetricSample(
                    _nextId++, _time.GetUtcNow(), reading.CpuPercent, reading.MemoryBytes,
                    reading.GpuPercent, reading.DeviceGpuPercent, reading.GpuMemoryBytes));
                while (_samples.Count > MaximumSamples) _samples.Dequeue();
            }
            Changed?.Invoke();
        }
        catch (Exception)
        {
            // A failed read skips one tick. Samplers report missing counters as nulls instead.
        }
        finally
        {
            Volatile.Write(ref _capturing, 0);
        }
    }
}
