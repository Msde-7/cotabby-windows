using Cotabby.Core.Input;

namespace Cotabby.Core.Tests.Input;

public sealed class KeyHoldTests
{
    private const int VkTab = 0x09;
    private const int VkShift = 0xA0;

    private static RawKey Down(int vk) => new(vk, vk + 0x100, IsKeyUp: false);
    private static RawKey Up(int vk) => new(vk, vk + 0x100, IsKeyUp: true);
    private static RawKey Down(char c) => Down((int)c);
    private static RawKey Up(char c) => Up((int)c);

    [Fact]
    public void WithoutAHold_EveryKeyIsDeliveredAsTyped()
    {
        var m = new Machine();
        m.Type(Down('x'), Up('x'));
        m.Run();

        Assert.Equal(["x↓", "x↑"], m.Host);
        Assert.False(m.Hold.IsHolding);
        Assert.Empty(m.Replays);
    }

    [Fact]
    public void KeysTypedDuringAnAccept_ReachHandlersAndTheHostAfterTheEdit_InOrder()
    {
        var m = new Machine();
        m.Type(Down(VkTab), Down('x'), Up('x'), Down(0x08), Up(0x08), Up(VkTab));
        m.Run();

        Assert.Empty(m.Host);
        Assert.Equal(["Tab↓"], m.Handled);
        m.Insert("lo");
        m.Accepts[0].Release();
        m.Run();

        Assert.Equal(["lo", "x↓", "x↑", "08↓", "08↑"], m.Host);
        Assert.Equal(["Tab↓", "x↓", "x↑", "08↓", "08↑"], m.Handled);
        Assert.False(m.Hold.IsHolding);
    }

    [Fact]
    public void AKeyHookedAfterTheBatchWasSentButQueuedBeforeIt_StillFollowsTheBatch()
    {
        var m = new Machine();
        m.Type(Down(VkTab), Down('x'));
        m.Run();
        // y is already in the system queue, ahead of the batch, but the hook has not reached it.
        m.Type(Down('y'));
        m.Insert("lo");
        m.Accepts[0].Release();
        m.Run();

        Assert.Equal(["lo", "x↓", "y↓"], m.Host);
    }

    [Fact]
    public void AReplayedAcceptKey_HoldsTheRestOfTheBatchBehindItsOwnEdit()
    {
        var m = new Machine();
        m.Type(Down(VkTab), Down('x'), Down(VkTab), Down('y'));
        m.Run();
        // w was typed before the batch went out and reaches the hook ahead of it.
        m.Type(Down('w'));
        m.Insert("one");
        m.Accepts[0].Release();
        m.Run();

        Assert.Equal(2, m.Accepts.Count);
        Assert.Equal(["one", "x↓"], m.Host);
        m.Type(Down('z'));
        m.Run();
        m.Insert("two");
        m.Accepts[1].Release();
        m.Run();

        Assert.Equal(["one", "x↓", "two", "y↓", "w↓", "z↓"], m.Host);
        Assert.Equal(["Tab↓", "x↓", "Tab↓", "y↓", "w↓", "z↓"], m.Handled);
        Assert.False(m.Hold.IsHolding);
    }

    [Fact]
    public void ReleasingOneOfTwoHolds_KeepsHoldingUntilTheOtherEnds()
    {
        var m = new Machine();
        m.Type(Down(VkTab), Down('x'));
        m.Run();
        var other = m.Hold.Begin();
        m.Accepts[0].Release();
        m.Run();

        Assert.Empty(m.Host);
        other.Release();
        m.Run();
        Assert.Equal(["x↓"], m.Host);
    }

    [Fact]
    public void AHoldNeverReleased_LetsTheKeysGoAfterASecond_AndItsLateReleaseChangesNothing()
    {
        var m = new Machine();
        m.Type(Down(VkTab), Down('x'), Up('x'));
        m.Run();

        m.Time.Advance(KeyHold.MaxHold - TimeSpan.FromMilliseconds(1));
        m.Run();
        Assert.Empty(m.Host);
        m.Time.Advance(TimeSpan.FromMilliseconds(1));
        m.Run();
        Assert.Equal(["x↓", "x↑"], m.Host);

        // A later accept holds again, and the stuck one ending does not end it.
        m.Type(Down(VkTab), Down('y'));
        m.Run();
        m.Accepts[0].Release();
        m.Run();
        Assert.Equal(["x↓", "x↑"], m.Host);
        m.Insert("lo");
        m.Accepts[1].Release();
        m.Run();
        Assert.Equal(["x↓", "x↑", "lo", "y↓"], m.Host);
    }

    [Fact]
    public void AQueueThatOverflows_LetsItsKeysGoEarly_InOrder_WithoutLosingAny()
    {
        var m = new Machine();
        m.Type(Down(VkTab));
        m.Run();
        var typed = Enumerable.Range(0, KeyHold.InitialCapacity + 40).Select(i => Down(0x1000 + i)).ToArray();
        // The hook sees each key before the next is typed, as a flood from a text-sending tool would come.
        foreach (var key in typed)
        {
            m.Type(key);
            m.Run();
        }

        Assert.Equal(typed.Select(Machine.Label), m.Host);
        m.Accepts[0].Release();
        m.Run();
        Assert.Equal(typed.Length, m.Host.Count);
        Assert.False(m.Hold.IsHolding);
    }

    [Fact]
    public void TheAcceptKeysOwnRelease_NeverReachesTheHost()
    {
        var m = new Machine();
        m.Type(Down(VkTab), Down('x'), Up(VkTab), Up('x'));
        m.Run();
        m.Insert("lo");
        m.Accepts[0].Release();
        m.Run();

        Assert.Equal(["lo", "x↓", "x↑"], m.Host);
        Assert.Equal(["Tab↑"], m.Drops);
    }

    [Fact]
    public void AReleaseAfterTheHoldEnded_PassesAsTyped()
    {
        var m = new Machine();
        m.Type(Down(VkTab));
        m.Run();
        m.Insert("lo");
        m.Accepts[0].Release();
        m.Type(Up(VkTab));
        m.Run();

        Assert.Equal(["lo", "Tab↑"], m.Host);
    }

    [Fact]
    public void AnAbandonedAccept_GivesItsKeyBackAheadOfTheKeysTypedAfterIt()
    {
        var m = new Machine();
        m.Type(Down(VkTab), Down('x'));
        m.Run();
        m.Accepts[0].GiveBackKey();
        m.Type(Up(VkTab), Up('x'));
        m.Run();
        m.Accepts[0].Release();
        m.Run();

        Assert.Equal(["Tab↓", "x↓", "Tab↑", "x↑"], m.Host);
        Assert.Empty(m.Drops);
    }

    [Fact]
    public void AnAbandonedAccept_WhoseReleaseIsAlreadyHeld_KeepsTheReleaseInItsPlace()
    {
        var m = new Machine();
        m.Type(Down(VkTab), Down('x'), Up(VkTab), Up('x'));
        m.Run();
        m.Accepts[0].GiveBackKey();
        m.Accepts[0].GiveBackKey();
        m.Accepts[0].Release();
        m.Run();

        Assert.Equal(["Tab↓", "x↓", "Tab↑", "x↑"], m.Host);
    }

    [Fact]
    public void AKeyGivenBackAfterItsReleaseWasDropped_IsSentWithARelease()
    {
        var m = new Machine();
        m.Type(Down(VkTab), Up(VkTab));
        m.Run();
        m.Accepts[0].Release();
        m.Run();
        m.Accepts[0].GiveBackKey();
        m.Run();

        Assert.Equal(["Tab↓", "Tab↑"], m.Host);
    }

    [Fact]
    public void AKeyGivenBackAfterTheHoldTimedOutAndItsReleasePassed_IsSentWithARelease()
    {
        var m = new Machine();
        m.Type(Down(VkTab));
        m.Run();
        m.Time.Advance(KeyHold.MaxHold);
        m.Run();
        m.Type(Up(VkTab));
        m.Run();
        m.Accepts[0].GiveBackKey();
        m.Run();

        // The host saw the lone release first, so the key it gets back is not left down.
        Assert.Equal(["Tab↑", "Tab↓", "Tab↑"], m.Host);
    }

    [Fact]
    public void HeldAutoRepeatsOfTheAcceptKey_ShareOneRelease_KeptWhenAnyOfThemWasGivenBack()
    {
        var m = new Machine();
        m.Type(Down(VkTab), Down(VkTab), Up(VkTab));
        m.Run();
        m.Accepts[0].GiveBackKey();
        m.Accepts[0].Release();
        m.Run();
        // The repeat accepted again on its replay. Its release is the same one.
        Assert.Equal(2, m.Accepts.Count);
        m.Insert("lo");
        m.Accepts[1].Release();
        m.Run();

        Assert.Equal(["Tab↓", "lo", "Tab↑"], m.Host);
    }

    [Fact]
    public void HeldAutoRepeatsOfTheAcceptKey_DropTheirReleaseWhenNoneWasGivenBack()
    {
        var m = new Machine();
        m.Type(Down(VkTab), Down(VkTab), Up(VkTab));
        m.Run();
        m.Insert("one");
        m.Accepts[0].Release();
        m.Run();
        m.Insert("two");
        m.Accepts[1].Release();
        m.Run();

        Assert.Equal(["one", "two"], m.Host);
        Assert.Equal(["Tab↑"], m.Drops);
    }

    [Fact]
    public void ModifiersTypedDuringAHold_KeepTheirPairsAndOrder()
    {
        var m = new Machine();
        m.Type(Down(VkShift));
        m.Run();
        m.Type(Down(VkTab), Up(VkShift), Down(VkShift), Down('A'), Up('A'), Up(VkShift));
        m.Run();
        m.Insert("lo");
        m.Accepts[0].Release();
        m.Run();

        Assert.Equal(["A0↓", "lo", "A0↑", "A0↓", "A↓", "A↑", "A0↑"], m.Host);
    }

    [Fact]
    public void ABatchTheSystemOnlyPartlyTook_DoesNotKeepHolding()
    {
        var m = new Machine { Accepting = 1 };
        m.Type(Down(VkTab), Down('x'), Down('y'));
        m.Run();
        m.Accepts[0].Release();
        m.Run();

        Assert.Equal(["x↓"], m.Host);
        Assert.False(m.Hold.IsHolding);
        m.Type(Down('z'));
        m.Run();
        Assert.Equal(["x↓", "z↓"], m.Host);
    }

    [Fact]
    public void ReplaysThatNeverComeBack_AreGivenUpOn_AndALateOneIsHandledAsTyped()
    {
        var m = new Machine();
        m.Type(Down(VkTab), Down('x'));
        m.Run();
        m.Accepts[0].Release();
        var lost = m.TakeQueue();
        Assert.True(m.Hold.IsHolding);

        m.Time.Advance(KeyHold.MaxReplayWait);
        Assert.False(m.Hold.IsHolding);
        m.Type(Down('y'));
        m.Run();
        m.Requeue(lost);
        m.Run();
        Assert.Equal(["y↓", "x↓"], m.Host);
    }

    [Fact]
    public void ReleaseAll_LetsEveryHeldKeyGo()
    {
        var m = new Machine();
        m.Type(Down(VkTab), Down('x'));
        m.Run();
        m.Hold.ReleaseAll();
        m.Run();

        Assert.Equal(["x↓"], m.Host);
        Assert.False(m.Hold.IsHolding);
    }

    /// <summary>
    /// The system input queue, a hook with one handler that accepts on Tab, and the host. The
    /// hook takes the events one at a time in queue order, as the raw input thread hands them over.
    /// </summary>
    private sealed class Machine : IKeyInjector
    {
        private readonly Queue<(RawKey Key, Source Source, string? Text)> _queue = new();

        public Machine()
        {
            Hold = new KeyHold(this, Time, schedule: work => work());
        }

        public enum Source { Typed, Replay, Passed, Inserted }

        public ManualTime Time { get; } = new();
        public KeyHold Hold { get; }

        /// <summary>What the host received, in order.</summary>
        public List<string> Host { get; } = [];

        /// <summary>What handlers saw, in order.</summary>
        public List<string> Handled { get; } = [];

        public List<string> Drops { get; } = [];
        public List<IKeyHold> Accepts { get; } = [];
        public List<RawKey> Replays { get; } = [];

        /// <summary>How many events of the next batch the system takes. All when null.</summary>
        public int? Accepting { get; set; }

        public static string Label(RawKey key)
        {
            string name = key.VirtualKey switch
            {
                VkTab => "Tab",
                >= 'A' and <= 'Z' or >= 'a' and <= 'z' => ((char)key.VirtualKey).ToString(),
                _ => key.VirtualKey.ToString("X2"),
            };
            return name + (key.IsKeyUp ? "↑" : "↓");
        }

        public void Type(params RawKey[] keys)
        {
            foreach (var key in keys) _queue.Enqueue((key, Source.Typed, null));
        }

        /// <summary>Cotabby's own edit, which the hook lets through untouched.</summary>
        public void Insert(string text) => _queue.Enqueue((default, Source.Inserted, text));

        public int Replay(ReadOnlySpan<RawKey> keys)
        {
            int taken = Math.Min(keys.Length, Accepting ?? keys.Length);
            Accepting = null;
            foreach (var key in keys[..taken])
            {
                Replays.Add(key);
                _queue.Enqueue((key, Source.Replay, null));
            }
            return taken;
        }

        public int Pass(ReadOnlySpan<RawKey> keys)
        {
            foreach (var key in keys) _queue.Enqueue((key, Source.Passed, null));
            return keys.Length;
        }

        public List<(RawKey, Source, string?)> TakeQueue()
        {
            var taken = _queue.ToList();
            _queue.Clear();
            return taken;
        }

        public void Requeue(List<(RawKey, Source, string?)> items)
        {
            foreach (var item in items) _queue.Enqueue(item);
        }

        public void Run()
        {
            while (_queue.TryDequeue(out var item))
            {
                switch (item.Source)
                {
                    case Source.Inserted:
                        Host.Add(item.Text!);
                        continue;
                    case Source.Passed:
                        Host.Add(Label(item.Key));
                        continue;
                }
                switch (Hold.Arrive(item.Key, item.Source == Source.Replay))
                {
                    case KeyRoute.Hold:
                        continue;
                    case KeyRoute.Drop:
                        Drops.Add(Label(item.Key));
                        continue;
                }
                Handled.Add(Label(item.Key));
                if (item.Key is { VirtualKey: VkTab, IsKeyUp: false })
                {
                    // The accept swallows the key and holds what follows until its edit is in.
                    Accepts.Add(Hold.Begin(item.Key));
                    continue;
                }
                Host.Add(Label(item.Key));
            }
        }
    }

    /// <summary>A clock whose timers fire when the test moves it past their time.</summary>
    private sealed class ManualTime : TimeProvider
    {
        private readonly List<ManualTimer> _timers = [];
        private long _now = 1_000_000;

        public override long TimestampFrequency => 1000;

        public override long GetTimestamp() => _now;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, () => callback(state));
            timer.Change(dueTime, period);
            _timers.Add(timer);
            return timer;
        }

        public void Advance(TimeSpan by)
        {
            _now += (long)by.TotalMilliseconds;
            foreach (var timer in _timers.ToList())
            {
                if (timer.DueAt is { } due && due <= _now)
                {
                    timer.DueAt = null;
                    timer.Fire();
                }
            }
        }

        private sealed class ManualTimer(ManualTime time, Action fire) : ITimer
        {
            public long? DueAt { get; set; }

            public void Fire() => fire();

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                DueAt = dueTime == Timeout.InfiniteTimeSpan ? null : time._now + (long)dueTime.TotalMilliseconds;
                return true;
            }

            public void Dispose() => DueAt = null;

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
