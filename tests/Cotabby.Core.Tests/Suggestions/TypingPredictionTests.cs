using Cotabby.Core.Focus;
using Cotabby.Core.Suggestions;

namespace Cotabby.Core.Tests.Suggestions;

public sealed class TypingPredictionTests
{
    private static FocusedField Field(string preceding, string trailing = "") =>
        Fields.Text(preceding + trailing) with { CaretOffset = preceding.Length };

    private static TypingPrediction Candidate(string preceding = "I'll send ") => new(Field(preceding), 0, CancellationToken.None);

    [Fact]
    public void MatchingKeysTrimOnlyAfterTheHostShowsThem()
    {
        var candidate = Candidate();
        const string prediction = "you the report tomorrow";
        Assert.True(candidate.Receive(prediction, final: false));
        Assert.True(candidate.Append("you ", 1, 1));
        Assert.True(candidate.Append("the ", 1.01, 2));

        Assert.True(candidate.Accepts(Field("I'll send you ")));
        Assert.Null(candidate.Rebased(prediction, Field("I'll send ")));
        Assert.Equal("report tomorrow", candidate.Rebased(prediction, Field("I'll send you the ")));
        Assert.Equal(("you the ", 2L), candidate.Progress);
    }

    [Fact]
    public void UncoveredTypingHasOneBoundedGracePeriod()
    {
        var candidate = Candidate();
        Assert.True(candidate.Append("y", 1, 1));
        Assert.True(candidate.Append("o", 1.1, 2));
        Assert.Equal(1 + TypingPrediction.CatchUpWindow, candidate.Expiration(Field("I'll send yo"))!.Value, 6);
        Assert.False(candidate.Append("u", 1.16, 3));

        Assert.True(candidate.Receive("you the report", final: false));
        Assert.Null(candidate.Expiration(Field("I'll send yo")));
        Assert.True(candidate.Append("u", 1.17, 3));
    }

    [Fact]
    public void CoveredTypingWaitsOnlyForTheHost()
    {
        var candidate = Candidate();
        Assert.True(candidate.Receive("you the report", final: false));
        Assert.True(candidate.Append("y", 1, 1));

        Assert.Equal(1 + TypingPrediction.PublicationWindow, candidate.Expiration(Field("I'll send "))!.Value, 6);
        Assert.False(candidate.IsPublished(Field("I'll send ")));
        Assert.True(candidate.IsPublished(Field("I'll send y")));
        Assert.Null(candidate.Expiration(Field("I'll send y")));
    }

    [Fact]
    public void DivergenceAndAFinalBehindTypingCannotBeReused()
    {
        var candidate = Candidate();
        Assert.True(candidate.Append("y", 1, 1));
        Assert.False(candidate.Receive("everyone a copy", final: false));
        Assert.False(candidate.Receive("", final: true));
        Assert.True(candidate.Receive("you the report", final: false));
        Assert.False(candidate.Append("e", 1.01, 2));
    }

    [Fact]
    public void SurrogatePairsTrimAsTheirUtf16Units()
    {
        var candidate = Candidate("Send ");
        const string prediction = "🐹 café tomorrow";
        Assert.True(candidate.Receive(prediction, final: false));
        Assert.True(candidate.Append("🐹 ", 1, 1));

        Assert.Equal("café tomorrow", candidate.Rebased(prediction, Field("Send 🐹 ")));
    }

    [Fact]
    public void ChangedFieldsSelectionTrailingTextAndOtherEditsFailClosed()
    {
        var candidate = Candidate("Hello ");
        FocusedField[] changed =
        [
            Field("Hello ") with { FieldId = "other" },
            Field("Hello ") with { ProcessId = 456 },
            Field("Hello ", "edited"),
            Field("Hello ") with { HasSelection = true },
            Field("Hello ") with { IsSecure = true },
            Field("Hello x"),
            Field("Hell"),
        ];

        Assert.All(changed, field => Assert.False(candidate.Accepts(field)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("\b")]
    [InlineData("\n")]
    [InlineData("\t")]
    public void ControlKeysAreRejected(string characters) =>
        Assert.False(Candidate().Append(characters, 1, 1));

    [Fact]
    public void TypingIsBounded()
    {
        var candidate = Candidate();
        Assert.False(candidate.Append(new string('a', TypingPrediction.MaximumTypedCharacters + 1), 1, 1));
        Assert.False(candidate.Append("a", double.PositiveInfinity, 1));
    }

    [Fact]
    public void OldPartialsCannotRollBackAndNothingOverwritesTheFinal()
    {
        var candidate = Candidate("Hello ");
        Assert.True(candidate.Receive("world again", final: false));
        Assert.True(candidate.Receive("world", final: false));
        Assert.True(candidate.Receive("world again tomorrow", final: true));

        Assert.False(candidate.Receive("world again tomorrow morning", final: false));
        Assert.False(candidate.Append("everyone", 1, 1));
        Assert.True(candidate.Append("world", 1, 1));
    }

    [Fact]
    public void ACancelledRequestKeepsNoKeys()
    {
        using var cts = new CancellationTokenSource();
        var candidate = new TypingPrediction(Field("I'll send "), 0, cts.Token);
        cts.Cancel();

        Assert.False(candidate.Append("y", 1, 1));
    }
}
