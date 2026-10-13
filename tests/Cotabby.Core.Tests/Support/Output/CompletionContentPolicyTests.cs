using Cotabby.Core.Support;

namespace Cotabby.Core.Tests;

public class CompletionContentPolicyTests
{
    private static CompletionContentRejection? Rejection(string completion, string precedingText) =>
        CompletionContentPolicy.Rejection(completion, precedingText);

    [Theory]
    [InlineData(",", "Best ")]
    [InlineData(":", "scheduled for ")]
    [InlineData(".", "Thanks for your patience")]
    [InlineData("…", "so")]
    public void PunctuationOnlyCompletionsAreRejected(string completion, string precedingText)
    {
        Assert.Equal(CompletionContentRejection.NoWordContent, Rejection(completion, precedingText));
    }

    [Fact]
    public void ClosingPunctuationRightAfterATypedSpaceIsRejected()
    {
        Assert.Equal(
            CompletionContentRejection.PunctuationAfterSpace,
            Rejection("! the view was beautiful", "we should do it again "));
        Assert.Null(Rejection("soon!", "we should do it again "));
        // No space was typed, so the model may be attaching punctuation to the word.
        Assert.Null(Rejection(", and", "we should do it again"));
        // Opening punctuation can start a word.
        Assert.Null(Rejection("(maybe)", "we should "));
    }

    [Fact]
    public void ScaffoldingAndMetaResponsesAreRejected()
    {
        Assert.Equal(CompletionContentRejection.Scaffolding, Rejection("[User 0001]", "asdkfj "));
        Assert.Equal(
            CompletionContentRejection.Scaffolding, Rejection("12:03 PM · 1 min read · Reply · 1 Like", "wfhqk "));
        Assert.Equal(
            CompletionContentRejection.Scaffolding,
            Rejection("<code>on_message</code> function.", "a race condition in the "));
        Assert.Equal(
            CompletionContentRejection.Scaffolding,
            Rejection("I'm not sure what you mean by \"the text\".", "xq vbnz "));
        // Markup in a markup field is content.
        Assert.Null(Rejection("<div>", "<p>hello</p> "));
        Assert.Null(Rejection("thing that's like a sandwich", "that new "));
    }

    [Fact]
    public void LoopingCompletionsAreRejected()
    {
        Assert.Equal(
            CompletionContentRejection.RepetitiveContent, Rejection(". Hi S. Hi S. Hi S.", "charlie. Hi S"));
        Assert.Equal(CompletionContentRejection.RepetitiveContent, Rejection("no no no no", "oh "));
        Assert.Equal(
            CompletionContentRejection.RepetitiveContent, Rejection("over and over and over and over", "again "));
        // Two copies are emphasis.
        Assert.Null(Rejection("very very good", "it was "));
        Assert.Null(Rejection("day after day, week after week", "it went on "));
    }

    [Fact]
    public void CompletionsCopiedFromTheRecentTextAreRejected()
    {
        Assert.Equal(
            CompletionContentRejection.CopiesPrecedingText,
            Rejection(". Hi Sarah, thanks for s.", "Field one. Hi Sarah, thanks for s"));
        Assert.Equal(
            CompletionContentRejection.CopiesPrecedingText,
            Rejection(" through the first two sections and", "I went through the first two sections and"));
        // Three shared words are ordinary phrasing.
        Assert.Null(Rejection(" for the invitation to the party.", "thanks for the draft. Thanks"));
        // Repeating a paragraph the document already holds is a prediction, not an echo.
        Assert.Null(Rejection(
            " Hi Sarah, thanks for sending over the draft",
            "Hi Sarah, thanks for sending over the draft yesterday.\nThe main thing is the timeline.\nHi"));
        // A four-word run inside a longer new thought stays under the copy threshold.
        Assert.Null(Rejection(
            " sending over the draft of the proposal", "Thanks for sending over the draft. I also want the "));
    }
}
