using Cotabby.Win32.Focus;

namespace Cotabby.Inference.Tests;

public class CaptureOrderTests
{
    [Fact]
    public void ACaptureThatStartedEarlier_DoesNotReplaceALaterOne()
    {
        // A refresh starts on the old field, focus moves, and the focus event's capture finishes first.
        var order = new CaptureOrder<string>();
        long refresh = order.Begin();
        long focusEvent = order.Begin();
        Assert.True(order.TryPublish(focusEvent, "password field"));
        Assert.False(order.TryPublish(refresh, "old field"));
        Assert.Equal("password field", order.Current);
    }

    [Fact]
    public void CapturesFinishingInOrder_AreEachPublished()
    {
        var order = new CaptureOrder<string>();
        Assert.True(order.TryPublish(order.Begin(), "a"));
        Assert.True(order.TryPublish(order.Begin(), "b"));
        Assert.True(order.TryPublish(order.Begin(), null));
        Assert.Null(order.Current);
    }

    [Fact]
    public void Reset_DropsCapturesStillRunning()
    {
        var order = new CaptureOrder<string>();
        long running = order.Begin();
        order.Reset();
        Assert.False(order.TryPublish(running, "stale"));
        Assert.Null(order.Current);
        Assert.True(order.TryPublish(order.Begin(), "fresh"));
    }

    [Fact]
    public void Deliver_PassesOnEachCaptureInTurn()
    {
        var order = new CaptureOrder<string>();
        var seen = new List<string?>();
        long first = order.Begin();
        order.TryPublish(first, "a");
        order.Deliver(first, seen.Add);
        long second = order.Begin();
        order.TryPublish(second, "b");
        order.Deliver(second, seen.Add);
        Assert.Equal(["a", "b"], seen);
    }

    [Fact]
    public void Deliver_DropsACaptureOnceALaterOneWasPassedOn()
    {
        // Tracking starts and reads focus, a focus event's read finishes and is passed on, and
        // only then is the start's read passed on. Listeners must end with the event's field.
        var order = new CaptureOrder<string>();
        var seen = new List<string?>();
        long start = order.Begin();
        order.TryPublish(start, "old field");
        long focusEvent = order.Begin();
        order.TryPublish(focusEvent, "new field");

        order.Deliver(focusEvent, seen.Add);
        order.Deliver(start, seen.Add);

        Assert.Equal(["new field"], seen);
    }

    [Fact]
    public void Deliver_PassesOnTheLaterValueForAnEarlierCaptureNotYetOvertaken()
    {
        var order = new CaptureOrder<string>();
        var seen = new List<string?>();
        long start = order.Begin();
        order.TryPublish(start, "old field");
        order.TryPublish(order.Begin(), "new field");

        order.Deliver(start, seen.Add);

        Assert.Equal(["new field"], seen);
    }

    [Fact]
    public void Deliver_DropsACaptureStartedBeforeAReset()
    {
        var order = new CaptureOrder<string>();
        var seen = new List<string?>();
        long running = order.Begin();
        order.Reset();
        order.Deliver(running, seen.Add);
        Assert.Empty(seen);
    }
}
