using Cotabby.App.Hosting;
using Cotabby.Core.Focus;
using Cotabby.Core.Overlay;

namespace Cotabby.Inference.Tests;

public sealed class SwitchableFocusTrackerTests
{
    private readonly CountingTracker _inner = new();
    private readonly List<FocusedField?> _events = [];
    private readonly SwitchableFocusTracker _tracker;

    public SwitchableFocusTrackerTests()
    {
        _tracker = new SwitchableFocusTracker(_inner);
        _tracker.FocusChanged += (_, field) => _events.Add(field);
    }

    [Fact]
    public void SwitchingOff_StopsTrackingAndTellsListenersFocusLeft()
    {
        _tracker.Start();
        _events.Clear();

        _tracker.SetEnabled(false);

        Assert.False(_inner.Running);
        Assert.Equal([null], _events);
    }

    [Fact]
    public void SwitchingOn_StartsTrackingAndTellsListenersWhereFocusIs()
    {
        _tracker.Start();
        _tracker.SetEnabled(false);
        _events.Clear();

        _tracker.SetEnabled(true);

        Assert.True(_inner.Running);
        Assert.Same(_inner.Field, Assert.Single(_events));
    }

    [Fact]
    public void StartWhileSwitchedOff_DoesNotTrack()
    {
        _tracker.SetEnabled(false);

        _tracker.Start();

        Assert.False(_inner.Running);
        _tracker.SetEnabled(true);
        Assert.True(_inner.Running);
    }

    [Fact]
    public void SwitchingOnAfterTheOwnerStopped_DoesNotTrack()
    {
        _tracker.Start();
        _tracker.SetEnabled(false);
        _tracker.Stop();

        _tracker.SetEnabled(true);

        Assert.False(_inner.Running);
        Assert.Equal([_inner.Field, null], _events);
    }

    [Fact]
    public void Start_TellsListenersWhereFocusIs()
    {
        _tracker.Start();

        Assert.True(_inner.Running);
        Assert.Same(_inner.Field, Assert.Single(_events));
    }

    [Fact]
    public void FocusChanges_ArePassedOn()
    {
        _tracker.Start();
        _events.Clear();

        _inner.RaiseFocus();

        Assert.Same(_inner.Field, Assert.Single(_events));
    }

    [Fact]
    public async Task QuittingWhileSwitchingOn_DoesNotWaitForTheFirstRead()
    {
        // Switching on reads the focused field, which a hung app can hold up for seconds.
        var inner = new HangingTracker();
        var tracker = new SwitchableFocusTracker(inner);
        tracker.SetEnabled(false);
        tracker.Start();
        var switching = Task.Run(() => tracker.SetEnabled(true));
        Assert.True(inner.Reading.Wait(TimeSpan.FromSeconds(5)));

        var stop = Task.Run(tracker.Stop);
        bool stopped = await Task.WhenAny(stop, Task.Delay(TimeSpan.FromSeconds(2))) == stop;
        inner.Release.Set();
        await switching;
        await stop;

        Assert.True(stopped, "Stop waited for the read");
        Assert.False(inner.Running);
    }

    [Fact]
    public async Task SwitchedOffDuringTheFirstRead_EndsStoppedWithFocusGone()
    {
        var inner = new HangingTracker();
        var tracker = new SwitchableFocusTracker(inner);
        var events = new List<FocusedField?>();
        tracker.FocusChanged += (_, field) => { lock (events) events.Add(field); };
        tracker.SetEnabled(false);
        tracker.Start();
        var switching = Task.Run(() => tracker.SetEnabled(true));
        Assert.True(inner.Reading.Wait(TimeSpan.FromSeconds(5)));

        var switchOff = Task.Run(() => tracker.SetEnabled(false));
        bool switchedOff = await Task.WhenAny(switchOff, Task.Delay(TimeSpan.FromSeconds(2))) == switchOff;
        inner.Release.Set();
        await switching;
        await switchOff;

        Assert.True(switchedOff, "switching off waited for the read");
        Assert.False(inner.Running);
        lock (events) Assert.Null(events[^1]);
    }

    /// <summary>Like the UI Automation tracker reading a hung app: starting blocks in its first read until released.</summary>
    private sealed class HangingTracker : IFocusTracker
    {
        private readonly FocusedField _field = new()
        {
            ProcessId = 7,
            ProcessName = "hung",
            Text = "",
            CaretOffset = 0,
            CaretRect = new ScreenRect(0, 0, 1, 20),
            FieldRect = new ScreenRect(0, 0, 800, 600),
            IsSingleLine = true,
            IsSecure = false,
        };

        private volatile bool _running;

        public ManualResetEventSlim Reading { get; } = new();
        public ManualResetEventSlim Release { get; } = new();
        public bool Running => _running;

        public FocusedField? Current => _running ? _field : null;

        public event EventHandler<FocusedField?>? FocusChanged;

        public FocusedField? Refresh(bool readHostText = false) => _field;

        public void Start()
        {
            if (_running) return;
            _running = true;
            Reading.Set();
            Release.Wait();
            if (_running) FocusChanged?.Invoke(this, _field);
        }

        public void Stop() => _running = false;

        public void Dispose() => Stop();
    }

    private sealed class CountingTracker : IFocusTracker
    {
        public FocusedField Field { get; } = new()
        {
            ProcessId = 42,
            ProcessName = "notepad",
            Text = "hello",
            CaretOffset = 5,
            CaretRect = new ScreenRect(0, 0, 1, 20),
            FieldRect = new ScreenRect(0, 0, 800, 600),
            IsSingleLine = false,
            IsSecure = false,
        };

        public bool Running { get; private set; }

        public FocusedField? Current => Running ? Field : null;

        public event EventHandler<FocusedField?>? FocusChanged;

        public FocusedField? Refresh(bool readHostText = false) => Field;

        public void RaiseFocus() => FocusChanged?.Invoke(this, Field);

        /// <summary>Like the UI Automation tracker, starting tells listeners where focus is.</summary>
        public void Start()
        {
            if (Running) return;
            Running = true;
            FocusChanged?.Invoke(this, Field);
        }

        public void Stop() => Running = false;

        public void Dispose() => Stop();
    }
}
