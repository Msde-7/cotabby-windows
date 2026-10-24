using Cotabby.Core.Suggestions;

namespace Cotabby.Core.Tests.Suggestions;

public sealed class DoubleTapAcceptanceStateTests
{
    private const string Session = "request-7";
    private static readonly double Window = DoubleTapAcceptanceState.Window.TotalSeconds;

    private static DoubleTapAcceptanceState Armed()
    {
        var state = new DoubleTapAcceptanceState();
        state.RecordWordAccept(Session, 10.0);
        return state;
    }

    [Fact]
    public void ASecondPressInsideTheWindowOnTheSameSession_IsADoubleTap() =>
        Assert.True(Armed().ConsumeDoubleTap(Session, 10.2));

    [Fact]
    public void ASecondPressAfterTheWindow_IsNot() =>
        Assert.False(Armed().ConsumeDoubleTap(Session, 10.0 + Window + 0.01));

    [Fact]
    public void APressOnADifferentSession_IsNot() =>
        Assert.False(Armed().ConsumeDoubleTap("request-8", 10.1));

    [Fact]
    public void AFirstPressWithNothingRecorded_IsNot() =>
        Assert.False(new DoubleTapAcceptanceState().ConsumeDoubleTap(Session, 10.0));

    [Fact]
    public void ConsumingEndsThePair_SoATripleTapIsNotASecondDoubleTap()
    {
        var state = Armed();

        Assert.True(state.ConsumeDoubleTap(Session, 10.1));
        Assert.False(state.ConsumeDoubleTap(Session, 10.2));
    }

    [Fact]
    public void AMissedDoubleTap_AlsoClearsThePendingPress()
    {
        var state = Armed();

        Assert.False(state.ConsumeDoubleTap(Session, 11.0));
        Assert.False(state.HasPendingPress);
    }

    [Fact]
    public void Reset_CancelsThePendingPress()
    {
        var state = Armed();

        state.Reset();

        Assert.False(state.ConsumeDoubleTap(Session, 10.1));
    }

    [Fact]
    public void AClockGoingBackwards_IsNotADoubleTap() =>
        Assert.False(Armed().ConsumeDoubleTap(Session, 9.9));
}
