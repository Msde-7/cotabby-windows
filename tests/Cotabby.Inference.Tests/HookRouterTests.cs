using Cotabby.Core.Input;
using Cotabby.Win32.Input;
using Cotabby.Win32.Insertion;

namespace Cotabby.Inference.Tests;

/// <summary>The keyboard hook's decisions before its handlers, against a model of the host's key state.</summary>
public class HookRouterTests
{
    private const int Tab = 0x09;
    private const int LeftShift = 0xA0;
    private const int A = 0x41;

    private static RawKey Down(int vk) => new(vk, 0x10, IsKeyUp: false);
    private static RawKey Up(int vk) => new(vk, 0x10, IsKeyUp: true);

    [Fact]
    public void CotabbysOwnKeys_PassUntouchedEvenWhileKeysAreHeld()
    {
        var m = new Machine();
        m.Hold.Begin();

        Assert.Equal(HookStep.Pass, m.Send(Down(A), InputMark.Input));
        Assert.Equal(HookStep.Suppress, m.Send(Down(A)));
    }

    [Fact]
    public void AModifierReleasedDuringAHold_IsReleasedAtTheHostAfterTheBatchPressesItAgain()
    {
        var m = new Machine();
        m.Send(Down(LeftShift));
        var hold = m.Hold.Begin();
        Assert.Equal(HookStep.Suppress, m.Send(Up(LeftShift)));

        // The edit's batch lets go of the Shift the user still seemed to hold, then presses it again.
        m.Send(Up(LeftShift), InputMark.Input);
        m.Send(Down(0), InputMark.Input);
        Assert.Equal(HookStep.Pass, m.Send(Down(LeftShift), InputMark.Restore));
        hold.Release();
        m.Pump();

        Assert.Empty(m.HostDown);
        Assert.False(m.Router.HeldKeys.IsHeld(LeftShift));
    }

    [Fact]
    public void AModifierPressedDuringAHold_IsNotPressedAgainAheadOfItsOwnPress()
    {
        var m = new Machine();
        var hold = m.Hold.Begin();
        Assert.Equal(HookStep.Suppress, m.Send(Down(LeftShift)));

        // A batch that saw the Shift as held releases it and presses it again. The hook has not
        // handed the press on yet, so it drops the press again; the held press follows instead.
        m.Send(Up(LeftShift), InputMark.Input);
        Assert.Equal(HookStep.Suppress, m.Send(Down(LeftShift), InputMark.Restore));
        hold.Release();
        m.Pump();
        Assert.Equal([LeftShift], m.HostDown);

        m.Send(Up(LeftShift));
        Assert.Empty(m.HostDown);
    }

    [Fact]
    public void TheSwallowedAcceptKeysRelease_IsDropped_AndTheNextPressIsNoRepeat()
    {
        var m = new Machine();
        Assert.Equal(HookStep.Raise, m.Send(Down(Tab), swallow: true));
        Assert.Equal(HookStep.Suppress, m.Send(Up(Tab)));
        m.Accepts[0].Release();
        m.Pump();

        Assert.Empty(m.HostDown);
        Assert.Empty(m.HostEvents);
        Assert.False(m.Router.HeldKeys.IsHeld(Tab));
        m.Send(Down(Tab), time: 100);
        Assert.False(m.LastRepeat);
    }

    [Fact]
    public void KeysHeldBehindAnEdit_ReachTheHandlersAsReplaysInOrder()
    {
        var m = new Machine();
        m.Send(Down(Tab), swallow: true);
        m.Send(Down(A));
        m.Send(Up(A));
        m.Send(Up(Tab));
        Assert.Single(m.Raised);
        m.Accepts[0].Release();
        m.Pump();

        Assert.Equal([(A, false, true), (A, true, true)], m.Raised.Skip(1));
        Assert.Equal(["41↓", "41↑"], m.HostEvents);
    }

    [Fact]
    public void ReplayedAutoRepeats_StillCountAsRepeats()
    {
        var m = new Machine();
        m.Send(Down(Tab), swallow: true);
        m.Send(Down(A), time: 100);
        m.Send(Down(A), time: 600);
        m.Accepts[0].Release();
        // Sent again together, the second press of the held key is still its auto-repeat.
        m.Pump(time: 700);

        Assert.True(m.LastRepeat);
    }

    [Theory]
    [InlineData(0x41, 0x1E, false, false, 0x41, 0x1E, 0u)]
    [InlineData(0x41, 0x1E, true, false, 0x41, 0x1E, 0x2u)] // KEYEVENTF_KEYUP
    [InlineData(0x25, 0x4B, false, true, 0x25, 0x4B, 0x1u)] // Left arrow, KEYEVENTF_EXTENDEDKEY
    [InlineData(0xA3, 0x1D, true, true, 0xA3, 0x1D, 0x3u)] // Right Ctrl up
    [InlineData(0xE7, 0x00E9, false, false, 0, 0x00E9, 0x4u)] // VK_PACKET "é", KEYEVENTF_UNICODE
    [InlineData(0xE7, 0xD83D, true, false, 0, 0xD83D, 0x6u)] // Half a surrogate pair, released
    public void AReplayedKey_IsSentAsTheHookReceivedIt(
        int vk, int scan, bool up, bool extended, int sentVk, int sentScan, uint flags)
    {
        var (virtualKey, scanCode, sentFlags) = KeySender.InputFor(new RawKey(vk, scan, up, extended));

        Assert.Equal(sentVk, virtualKey);
        Assert.Equal(sentScan, scanCode);
        Assert.Equal(flags, sentFlags);
    }

    /// <summary>The hook thread with the system input queue in front of it and the host behind it.</summary>
    private sealed class Machine : IKeyInjector
    {
        private readonly Queue<RawKey> _replays = new();

        public Machine()
        {
            Hold = new KeyHold(this, schedule: work => work());
            Router = new HookRouter(Hold);
        }

        public KeyHold Hold { get; }
        public HookRouter Router { get; }

        /// <summary>Keys the host was told are down.</summary>
        public SortedSet<int> HostDown { get; } = [];

        /// <summary>Events the host received from the handlers, as typed or replayed.</summary>
        public List<string> HostEvents { get; } = [];

        public List<(int Vk, bool Up, bool Replay)> Raised { get; } = [];
        public List<IKeyHold> Accepts { get; } = [];
        public bool LastRepeat { get; private set; }

        public HookStep Send(RawKey key, InputMark mark = InputMark.None, bool swallow = false, uint time = 0)
        {
            var step = Router.Route(key, mark, time, out bool repeat);
            switch (step)
            {
                case HookStep.Pass:
                    ToHost(key);
                    break;
                case HookStep.Raise:
                    LastRepeat = repeat;
                    Raised.Add((key.VirtualKey, key.IsKeyUp, mark == InputMark.Replay));
                    if (swallow)
                    {
                        Accepts.Add(Hold.Begin(key));
                        break;
                    }
                    ToHost(key);
                    HostEvents.Add($"{key.VirtualKey:X2}{(key.IsKeyUp ? "↑" : "↓")}");
                    break;
            }
            return step;
        }

        /// <summary>The hook handles the replays in the system queue.</summary>
        public void Pump(uint time = 0)
        {
            while (_replays.TryDequeue(out var key)) Send(key, InputMark.Replay, time: time);
        }

        public int Replay(ReadOnlySpan<RawKey> keys)
        {
            foreach (var key in keys) _replays.Enqueue(key);
            return keys.Length;
        }

        public int Pass(ReadOnlySpan<RawKey> keys)
        {
            foreach (var key in keys) ToHost(key);
            return keys.Length;
        }

        private void ToHost(RawKey key)
        {
            if (key.IsKeyUp) HostDown.Remove(key.VirtualKey);
            else if (key.VirtualKey != 0) HostDown.Add(key.VirtualKey);
        }
    }
}
