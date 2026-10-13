using Cotabby.Core.Suggestions;

namespace Cotabby.Core.Tests.Suggestions;

[Collection(SpacelessSegmenter.Collection)]
public sealed class AcceptanceChunkerPhraseTests
{
    [Theory]
    [InlineData("", "")]
    [InlineData("hello world again", "hello world again")]
    public void NextPhrase_WithNoBoundary_TakesTheWholeTail(string tail, string expected) =>
        Assert.Equal(expected, AcceptanceChunker.NextPhrase(tail));

    [Theory]
    [InlineData("hello world. foo bar.", "hello world.")]
    [InlineData("how are you? fine.", "how are you?")]
    [InlineData("stop! go back", "stop!")]
    [InlineData("  hello. world.", "  hello.")]
    [InlineData("don't go. yes", "don't go.")]
    [InlineData("hello, world. next", "hello, world.")]
    public void NextPhrase_StopsAtTheFirstSentenceTerminator(string tail, string expected) =>
        Assert.Equal(expected, AcceptanceChunker.NextPhrase(tail));

    [Theory]
    [InlineData("hello\nworld", "hello\n")]
    [InlineData("\nworld", "\n")]
    [InlineData("\n\nbody", "\n")]
    [InlineData("  \n", "  \n")]
    [InlineData("hello world\nmore", "hello world\n")]
    [InlineData("done.\nNext", "done.")]
    public void NextPhrase_StopsJustAfterTheFirstNewline(string tail, string expected) =>
        Assert.Equal(expected, AcceptanceChunker.NextPhrase(tail));

    [Theory]
    [InlineData("Step 1. Open the app. Then", "Step 1. Open the app.")]
    [InlineData("e.g. this works. Next", "e.g. this works.")]
    [InlineData("Ask Dr. Smith today. Then", "Ask Dr. Smith today.")]
    [InlineData("U.S.A. is great.", "U.S.A. is great.")]
    public void NextPhrase_WalksPastPeriodsThatDoNotEndASentence(string tail, string expected) =>
        Assert.Equal(expected, AcceptanceChunker.NextPhrase(tail));

    [Theory]
    [InlineData("\"done.\" Next sentence.", "\"done.\"")]
    [InlineData("“done.” Next.", "“done.”")]
    [InlineData("(yes!) keep going", "(yes!)")]
    [InlineData("(\"done.\") next", "(\"done.\")")]
    public void NextPhrase_StopsAfterTheClosersThatFollowATerminator(string tail, string expected) =>
        Assert.Equal(expected, AcceptanceChunker.NextPhrase(tail));

    [Theory]
    [InlineData("\"hi\" there", "\"hi\" there")]
    [InlineData("\"\" hello", "\"\" hello")]
    public void NextPhrase_ClosersWithNoTerminatorUnderneathAreNotABoundary(string tail, string expected) =>
        Assert.Equal(expected, AcceptanceChunker.NextPhrase(tail));

    [Theory]
    [InlineData("理解し、その内容を自分の言葉で表現する。", "理解し、")]
    [InlineData("その内容を自分の言葉で表現する。次の文", "その内容を自分の言葉で表現する。")]
    [InlineData("すごい！次へ", "すごい！")]
    [InlineData("いいですか？はい", "いいですか？")]
    [InlineData("終わり。」次の文", "終わり。」")]
    [InlineData("資料を読み､次へ", "資料を読み､")]
    [InlineData("終わり｡｣次の文", "終わり｡｣")]
    public void NextPhrase_StopsAtCjkClauseCommasAndSentenceTerminators(string tail, string expected) =>
        Assert.Equal(expected, AcceptanceChunker.NextPhrase(tail));

    [Theory]
    [InlineData("you? Yes.", "you?")]
    [InlineData("理解し、その内容を自分の言葉で表現する。", "理解し、")]
    public void NextPhrase_IsTheSameWithTrailingPunctuationOnOrOff(string tail, string expected)
    {
        Assert.Equal(expected, AcceptanceChunker.NextPhrase(tail, autoAcceptTrailingPunctuation: true));
        Assert.Equal(expected, AcceptanceChunker.NextPhrase(tail, autoAcceptTrailingPunctuation: false));
    }
}
