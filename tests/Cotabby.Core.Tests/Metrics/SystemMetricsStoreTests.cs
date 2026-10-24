using Cotabby.Core.Metrics;

namespace Cotabby.Core.Tests;

public class SystemMetricsStoreTests
{
    /// <summary>Each reading is numbered so a test can tell which read made which sample.</summary>
    private sealed class FakeSampler(IReadOnlyList<double?>? gpu = null) : ISystemResourceSampler
    {
        public int Reads { get; private set; }
        public bool Disposed { get; private set; }

        public SystemResourceReading Read()
        {
            Reads++;
            if (gpu is null) return new SystemResourceReading(Reads, Reads * 100L);
            double? share = gpu[Math.Min(Reads - 1, gpu.Count - 1)];
            return new SystemResourceReading(Reads, Reads * 100L, share, 42, 1_024);
        }

        public void Dispose() => Disposed = true;
    }

    /// <summary>A clock whose timers fire only when the test says so, on the test thread.</summary>
    private sealed class ManualTime : TimeProvider
    {
        public List<ManualTimer> Timers { get; } = [];

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(() => callback(state));
            Timers.Add(timer);
            return timer;
        }

        /// <summary>Fires every live timer once, like one tick of the real interval.</summary>
        public void Tick()
        {
            foreach (var timer in Timers.Where(t => !t.Disposed).ToList()) timer.Fire();
        }
    }

    private sealed class ManualTimer(Action fire) : ITimer
    {
        public bool Disposed { get; private set; }

        public void Fire() => fire();

        public bool Change(TimeSpan dueTime, TimeSpan period) => true;

        public void Dispose() => Disposed = true;

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private static (SystemMetricsStore Store, ManualTime Time, List<FakeSampler> Samplers) MakeStore(
        IReadOnlyList<double?>? gpu = null)
    {
        var time = new ManualTime();
        var samplers = new List<FakeSampler>();
        var store = new SystemMetricsStore(() =>
        {
            var sampler = new FakeSampler(gpu);
            samplers.Add(sampler);
            return sampler;
        }, timeProvider: time);
        return (store, time, samplers);
    }

    [Fact]
    public void StartsEmptyWithoutATimer()
    {
        var (store, time, _) = MakeStore();
        Assert.Empty(store.Samples);
        Assert.Empty(time.Timers);
    }

    [Fact]
    public void FirstViewerStartsOneTimerAndItsFirstTickReads()
    {
        var (store, time, samplers) = MakeStore();
        store.BeginSampling();
        time.Tick();

        var sample = Assert.Single(store.Samples);
        Assert.Equal(0, sample.Id);
        Assert.Equal(1, sample.CpuPercent);
        Assert.Equal(100, sample.MemoryBytes);
        Assert.Single(time.Timers);
        Assert.Single(samplers);
    }

    [Fact]
    public void SecondViewerSharesTheRunningSession()
    {
        var (store, time, samplers) = MakeStore();
        store.BeginSampling();
        store.BeginSampling();
        time.Tick();

        // The second viewer must not start a second timer or a duplicate read.
        Assert.Single(time.Timers);
        Assert.Equal(1, samplers[0].Reads);

        store.EndSampling();
        Assert.Single(store.Samples);
        Assert.False(time.Timers[0].Disposed);

        // The last viewer leaving stops the timer and drops the stale window.
        store.EndSampling();
        Assert.Empty(store.Samples);
        Assert.True(time.Timers[0].Disposed);
    }

    [Fact]
    public void UnbalancedEndSamplingClampsAndStaysUsable()
    {
        var (store, time, _) = MakeStore();
        store.EndSampling();
        Assert.Empty(store.Samples);

        store.BeginSampling();
        time.Tick();
        Assert.Single(store.Samples);
        store.EndSampling();
        Assert.Empty(store.Samples);
    }

    [Fact]
    public void ANewSessionRestartsIdsAndUsesAFreshSampler()
    {
        var (store, time, samplers) = MakeStore(gpu: [null, 50]);
        store.BeginSampling();
        time.Tick();
        time.Tick();
        Assert.Equal(50, store.Samples[^1].GpuPercent);
        store.EndSampling();

        store.BeginSampling();
        time.Tick();

        // A fresh sampler has no GPU baseline, so the time the pane was closed is never averaged in.
        var sample = Assert.Single(store.Samples);
        Assert.Equal(0, sample.Id);
        Assert.Null(sample.GpuPercent);
        Assert.Equal(2, samplers.Count);
        Assert.True(SpinWait.SpinUntil(() => samplers[0].Disposed, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void ATickFromAnEndedSessionIsIgnored()
    {
        var (store, time, _) = MakeStore();
        store.BeginSampling();
        var stale = time.Timers[0];
        store.EndSampling();

        stale.Fire();
        Assert.Empty(store.Samples);
    }

    [Fact]
    public void ClearDropsTheWindowWithoutStoppingSampling()
    {
        var (store, time, _) = MakeStore();
        store.BeginSampling();
        time.Tick();
        store.Clear();
        Assert.Empty(store.Samples);

        time.Tick();
        Assert.Equal(0, Assert.Single(store.Samples).Id);
    }

    [Fact]
    public void WindowKeepsTheNewestSamplesInOrder()
    {
        var (store, time, _) = MakeStore();
        store.BeginSampling();
        for (int i = 0; i < SystemMetricsStore.MaximumSamples + 5; i++) time.Tick();

        var ids = store.Samples.Select(s => s.Id).ToList();
        Assert.Equal(SystemMetricsStore.MaximumSamples, ids.Count);
        Assert.Equal(Enumerable.Range(5, SystemMetricsStore.MaximumSamples).Select(i => (long)i), ids);
    }

    [Fact]
    public void GpuFiguresAreCarriedOnEverySample()
    {
        var (store, time, _) = MakeStore(gpu: [null, 25, null, 50]);
        store.BeginSampling();
        for (int i = 0; i < 4; i++) time.Tick();

        // A missing share stays missing rather than turning into 0%.
        Assert.Equal([null, 25, null, 50], store.Samples.Select(s => s.GpuPercent));
        Assert.All(store.Samples, s => Assert.Equal(42, s.DeviceGpuPercent));
        Assert.All(store.Samples, s => Assert.Equal(1_024, s.GpuMemoryBytes));
    }

    [Fact]
    public void UnavailableGpuLeavesEveryGpuFieldEmpty()
    {
        var (store, time, _) = MakeStore();
        store.BeginSampling();
        time.Tick();

        var sample = Assert.Single(store.Samples);
        Assert.Null(sample.GpuPercent);
        Assert.Null(sample.DeviceGpuPercent);
        Assert.Null(sample.GpuMemoryBytes);
    }

    [Fact]
    public void ChangedIsRaisedForEachSample()
    {
        var (store, time, _) = MakeStore();
        int changes = 0;
        store.Changed += () => changes++;
        store.BeginSampling();
        time.Tick();
        time.Tick();
        Assert.Equal(2, changes);
    }

    [Fact]
    public void RealTimerDeliversContiguousSamples()
    {
        var sampler = new FakeSampler();
        using var store = new SystemMetricsStore(() => sampler, TimeSpan.FromMilliseconds(10));
        store.BeginSampling();

        Assert.True(SpinWait.SpinUntil(() => store.Samples.Count >= 3, TimeSpan.FromSeconds(10)));
        var ids = store.Samples.Select(s => s.Id).ToList();
        Assert.Equal(Enumerable.Range(0, ids.Count).Select(i => (long)i), ids);
        store.EndSampling();
    }
}
