using Cotabby.Core.Input;
using Cotabby.Core.Suggestions;
using Outcome = Cotabby.Core.Suggestions.SuggestionSessionReconciler.Outcome;

namespace Cotabby.Core.Tests.Suggestions;

public sealed class SuggestionSessionReconcilerTests
{
    private static readonly SuggestionSession Session = new()
    {
        RequestId = "request",
        VisibleText = "world",
    };

    [Fact]
    public void Apply_TypingTheNextCharacter_AdvancesPastIt()
    {
        var result = SuggestionSessionReconciler.Apply(Session, Keys.Char('w'));

        Assert.Equal(Outcome.Advance, result.Outcome);
        Assert.Equal(Session with { VisibleText = "orld" }, result.Next);
    }

    [Fact]
    public void Apply_TypingTheLastCharacter_AdvancesToAnEmptySuggestion()
    {
        var result = SuggestionSessionReconciler.Apply(Session with { VisibleText = "d" }, Keys.Char('d'));

        Assert.Equal(Outcome.Advance, result.Outcome);
        Assert.Equal("", result.Next?.VisibleText);
    }

    [Theory]
    [InlineData('x')]
    [InlineData('W')]
    public void Apply_TypingAnyOtherCharacter_Cancels(char typed) =>
        Assert.Equal(new(Outcome.Cancel, null), SuggestionSessionReconciler.Apply(Session, Keys.Char(typed)));

    [Fact]
    public void Apply_TypingIntoAnEmptySuggestion_Cancels() =>
        Assert.Equal(
            new(Outcome.Cancel, null),
            SuggestionSessionReconciler.Apply(Session with { VisibleText = "" }, Keys.Char('w')));

    [Theory]
    [InlineData(KeyKind.Tab)]
    [InlineData(KeyKind.Backspace)]
    [InlineData(KeyKind.Delete)]
    [InlineData(KeyKind.Escape)]
    [InlineData(KeyKind.Enter)]
    [InlineData(KeyKind.Arrow)]
    public void Apply_EditingAndNavigationKeys_Cancel(KeyKind kind) =>
        Assert.Equal(new(Outcome.Cancel, null), SuggestionSessionReconciler.Apply(Session, Keys.Special(kind)));

    [Fact]
    public void Apply_OtherKeys_LeaveTheSessionAlone() =>
        Assert.Equal(new(Outcome.Ignore, Session), SuggestionSessionReconciler.Apply(Session, Keys.Special(KeyKind.Other)));

    [Theory]
    [InlineData(KeyKind.Character)]
    [InlineData(KeyKind.Escape)]
    public void Apply_KeyUp_LeavesTheSessionAlone(KeyKind kind)
    {
        var release = Keys.Special(kind) with { Character = 'w', IsKeyDown = false };

        Assert.Equal(new(Outcome.Ignore, Session), SuggestionSessionReconciler.Apply(Session, release));
    }

    [Fact]
    public void Apply_TypingASpace_AdvancesPastANonBreakingSpace()
    {
        var result = SuggestionSessionReconciler.Apply(Session with { VisibleText = "\u00A0world" }, Keys.Char(' '));

        Assert.Equal(Outcome.Advance, result.Outcome);
        Assert.Equal("world", result.Next?.VisibleText);
    }

    [Theory]
    [InlineData("Hello there", "Hello there", 0, true)]
    [InlineData("Hello there", "Hello\u00A0there", 0, true)]
    [InlineData("Hello\u00A0", "Hello ", 0, true)]
    [InlineData("Hello there", "Hello th", 3, true)]
    [InlineData("Hello there", "Hello th", 2, false)]
    [InlineData("Hello there", "Hello_th", 8, false)]
    public void FollowsExpected_TreatsANonBreakingSpaceAsASpace(string expected, string live, int lag, bool follows) =>
        Assert.Equal(follows, SuggestionSessionReconciler.FollowsExpected(expected, live, lag));

    [Fact]
    public void SpaceNormalized_KeepsOffsets() =>
        Assert.Equal("a b c", SuggestionSessionReconciler.SpaceNormalized("a\u00A0b c"));
}
