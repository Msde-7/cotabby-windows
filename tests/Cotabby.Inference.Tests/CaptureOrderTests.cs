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
}
