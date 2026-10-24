using Cotabby.Core.History;

namespace Cotabby.Core.Tests.History;

public sealed class TypingHistoryPhrasePredictorTests
{
    private static readonly TypingHistoryPhrasePredictor.Limits SingleLine = new(MaxWords: 12, AllowsNewlines: false);
    private static readonly TypingHistoryPhrasePredictor.Limits MultiLine = new(MaxWords: 12, AllowsNewlines: true);

    private static TypingHistoryRecord Record(string text, int? typedLength = null) => new()
    {
        Id = Guid.NewGuid(),
        BundleIdentifier = "mail",
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
        Text = text,
        Source = TypingHistorySource.Imported,
        TypedLength = typedLength,
    };

    private static TypingHistoryPhrasePredictor Predictor(IEnumerable<string> texts) =>
        new(texts.Select(text => Record(text)));

    private static IEnumerable<string> Repeated(string text, int count) => Enumerable.Repeat(text, count);

    [Fact]
    public void RepeatedPhraseIsCompletedAfterAWordBoundary()
    {
        var history = Repeated("Thanks again, please let me know if you have any questions.", 4);

        Assert.Equal(
            "if you have any questions.",
            Predictor(history).Continuation("Sure, please let me know ", SingleLine));
    }

    [Fact]
    public void PartialWordIsFinishedWithOnlyTheUntypedLetters()
    {
        var history = Repeated("Thanks again, please let me know if you have any questions.", 4);

        Assert.Equal(
            "ve any questions.",
            Predictor(history).Continuation("Sure, please let me know if you ha", SingleLine));
    }

    [Fact]
    public void FullyTypedWordWithoutSpaceGetsALeadingSpace()
    {
        var history = Repeated("Thanks again, please let me know if you have any questions.", 4);

        Assert.Equal(
            " you have any questions.",
            Predictor(history).Continuation("Sure, please let me know if", SingleLine));
    }

    [Fact]
    public void AmbiguousContinuationIsNotOffered()
    {
        string[] history =
        [
            "please let me know if it works",
            "please let me know when it ships",
            "please let me know what you think",
            "please let me know how it goes",
        ];

        Assert.Null(Predictor(history).Continuation("please let me know ", SingleLine));
    }

    [Fact]
    public void TooFewOccurrencesAreNotOffered()
    {
        var history = Repeated("please let me know if you have any questions.", 2);

        Assert.Null(Predictor(history).Continuation("please let me know ", SingleLine));
    }

    [Fact]
    public void SingleWordShortcutNeedsStrongerEvidence()
    {
        var three = Repeated("Thanks for the update.\nBest regards, Senad\nImperum", 3);
        var six = Repeated("Thanks for the update.\nBest regards, Senad\nImperum", 6);

        Assert.Null(Predictor(three).Continuation("Thanks!\nBest regards, ", SingleLine));
        Assert.Equal("Senad", Predictor(six).Continuation("Thanks!\nBest regards, ", SingleLine));
    }

    [Fact]
    public void NewlineContinuesOnlyWhenMultiLineIsAllowed()
    {
        var history = Repeated("Thanks for your time.\nKind regards,\nSenad Aruc\nImperum B.V.", 6);

        Assert.Null(Predictor(history).Continuation("Thanks for your time.\nKind regards,", SingleLine));
        Assert.Equal(
            "\nSenad Aruc\nImperum B.V.",
            Predictor(history).Continuation("Thanks for your time.\nKind regards,", MultiLine));
    }

    [Fact]
    public void OutputUsesTheUsersSpellingAndRespectsTheWordLimit()
    {
        var history = Repeated("we will start the imperum POC with the SOC team next Monday morning", 4);

        Assert.Equal(
            "imperum POC",
            Predictor(history).Continuation("Then we will start the ", new(MaxWords: 2, AllowsNewlines: false)));
    }

    [Fact]
    public void TwoWordFallbackAnswersWhenTheThirdWordIsNew()
    {
        // "Kind regards," follows many different sentences, so the three-word context before it is
        // rarely the same. The two-word fallback still knows what comes next.
        var history = Enumerable.Range(0, 6).Select(i => $"Topic number {i} is done.\nKind regards,\nSenad");

        Assert.Equal(
            "\nSenad",
            Predictor(history).Continuation("Something never seen before.\nKind regards,", MultiLine));
    }

    [Fact]
    public void OnlyTextTypedBeforeTheCaretIsLearned()
    {
        // The quoted thread after the caret is someone else's writing. Their name must not become
        // the user's sign-off.
        const string typed = "Thanks for the update.\nBest regards, Senad";
        const string quoted = "\n\nOn Monday Luuk wrote:\nThanks for the update.\nBest regards, Luuk";
        var records = Enumerable.Range(0, 6).Select(_ => Record(typed + quoted, typed.Length));

        Assert.Equal(
            "Senad",
            new TypingHistoryPhrasePredictor(records).Continuation("Done.\nBest regards, ", SingleLine));
    }

    [Fact]
    public void UnknownContextReturnsNull()
    {
        Assert.Null(Predictor(["hello there general kenobi"]).Continuation("completely new words ", SingleLine));
        Assert.Null(Predictor([]).Continuation("", SingleLine));
    }
}
