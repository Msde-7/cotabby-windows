using Cotabby.Core.Focus;
using static Cotabby.Core.Focus.FocusFlickerGate.Decision;

namespace Cotabby.Core.Tests.Focus;

public sealed class FocusFlickerGateTests
{
    [Fact]
    public void FirstReadIsAlwaysApplied()
    {
        var gate = new FocusFlickerGate();
        Assert.Equal(Apply, gate.Evaluate("A", captured: true));
    }

    [Fact]
    public void OneFailedReadOnTheSameFieldIsSuppressed()
    {
        var gate = new FocusFlickerGate();
        gate.Evaluate("A", true);
        Assert.Equal(Suppress, gate.Evaluate("A", false));
    }

    [Fact]
    public void AGoodReadAfterAFlickerRearmsTheGate()
    {
        var gate = new FocusFlickerGate();
        gate.Evaluate("A", true);
        gate.Evaluate("A", false);
        Assert.Equal(Apply, gate.Evaluate("A", true));
        // A fresh flicker starts its own count.
        Assert.Equal(Suppress, gate.Evaluate("A", false));
    }

    [Fact]
    public void TwoFailedReadsInARowReleaseTheLoss()
    {
        var gate = new FocusFlickerGate();
        gate.Evaluate("A", true);
        Assert.Equal(Suppress, gate.Evaluate("A", false));
        Assert.Equal(Apply, gate.Evaluate("A", false));
    }

    [Fact]
    public void AfterReleaseFailedReadsKeepApplyingUntilAGoodRead()
    {
        var gate = new FocusFlickerGate();
        gate.Evaluate("A", true);
        gate.Evaluate("A", false);
        gate.Evaluate("A", false);
        Assert.Equal(Apply, gate.Evaluate("A", false));
    }

    [Fact]
    public void AGoodReadOnANewFieldMovesTheReference()
    {
        var gate = new FocusFlickerGate();
        gate.Evaluate("A", true);
        gate.Evaluate("B", true);
        Assert.Equal(Suppress, gate.Evaluate("B", false));
        Assert.Equal(Apply, gate.Evaluate("A", false));
    }

    [Fact]
    public void AFailedReadOnAnotherElementIsApplied()
    {
        var gate = new FocusFlickerGate();
        gate.Evaluate("A", true);
        Assert.Equal(Apply, gate.Evaluate("B", false));
    }

    [Fact]
    public void AFailedReadWithoutAPriorGoodReadIsApplied()
    {
        var gate = new FocusFlickerGate();
        Assert.Equal(Apply, gate.Evaluate("A", false));
    }

    [Fact]
    public void AFailedReadWithNoElementIsAppliedAndClearsThePendingCount()
    {
        var gate = new FocusFlickerGate();
        gate.Evaluate("A", true);
        gate.Evaluate("A", false);
        Assert.Equal(Apply, gate.Evaluate(null, false));
        Assert.Equal(Apply, gate.Evaluate("A", false));
    }

    [Fact]
    public void AGoodReadWithoutAnIdDoesNotArmTheGate()
    {
        var gate = new FocusFlickerGate();
        Assert.Equal(Apply, gate.Evaluate("", true));
        Assert.Equal(Apply, gate.Evaluate("A", false));
    }
}
