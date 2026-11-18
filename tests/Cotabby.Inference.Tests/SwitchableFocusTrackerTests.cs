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
        Assert.Equal([null], _events);
    }

    [Fact]
    public void FocusChanges_ArePassedOn()
    {
        _tracker.Start();

        _inner.RaiseFocus();

        Assert.Same(_inner.Field, Assert.Single(_events));
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

        public void Start() => Running = true;

        public void Stop() => Running = false;

        public void Dispose() => Stop();
    }
}
