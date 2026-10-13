using Cotabby.Core.Suggestions;

namespace Cotabby.Core.Tests.Suggestions;

[Collection(SpacelessSegmenter.Collection)]
public sealed class AcceptanceChunkerWordTests
{
    [Theory]
    [InlineData("  world again", "  world")]
    [InlineData("world again", "world")]
    [InlineData("", "")]
    [InlineData("you?", "you?")]
    public void NextChunk_TakesLeadingWhitespaceAndTheNextToken(string tail, string expected)
    {
        Assert.Equal(expected, AcceptanceChunker.NextChunk(tail));
        Assert.Equal(expected, AcceptanceChunker.NextChunk(tail, autoAcceptTrailingPunctuation: true));
    }

    [Theory]
    [InlineData(". I'll be in touch", ". I'll")]
    [InlineData(", and then", ", and")]
    [InlineData("!", "!")]
    [InlineData(" ... maybe", " ... maybe")]
    public void NextChunk_BindsLeadingPunctuationToTheNextWord(string tail, string expected) =>
        Assert.Equal(expected, AcceptanceChunker.NextChunk(tail));

    [Fact]
    public void NextChunk_PunctuationOff_KeepsLeadingPunctuationAsItsOwnStep() =>
        Assert.Equal(".", AcceptanceChunker.NextChunk(". Done.", autoAcceptTrailingPunctuation: false));

    [Theory]
    [InlineData("\n\nbody text", "\n\nbody")]
    [InlineData("  \t", "  \t")]
    public void NextChunk_LeadingNewlinesRideAlongAndWhitespaceOnlyTailsAreTakenWhole(string tail, string expected)
    {
        Assert.Equal(expected, AcceptanceChunker.NextChunk(tail, autoAcceptTrailingPunctuation: true));
        Assert.Equal(expected, AcceptanceChunker.NextChunk(tail, autoAcceptTrailingPunctuation: false));
    }

    [Theory]
    [InlineData("you?", "you")]
    [InlineData("?", "?")]
    [InlineData("you?!", "you")]
    [InlineData("?!", "?!")]
    [InlineData("don't", "don't")]
    [InlineData("U.S.A", "U.S.A")]
    [InlineData("U.S.A.", "U.S.A")]
    [InlineData(" world!", " world")]
    [InlineData("hello world?", "hello")]
    public void NextChunk_PunctuationOff_SplitsOnlyTrailingPunctuation(string tail, string expected) =>
        Assert.Equal(expected, AcceptanceChunker.NextChunk(tail, autoAcceptTrailingPunctuation: false));

    [Theory]
    [InlineData("café!", "café")]
    [InlineData("नमस्ते दुनिया", "नमस्ते")]
    public void NextChunk_PunctuationOff_KeepsCombiningMarksWithTheirLetter(string tail, string expected) =>
        Assert.Equal(expected, AcceptanceChunker.NextChunk(tail, autoAcceptTrailingPunctuation: false));

    [Theory]
    [InlineData("hello world", "hello")]
    [InlineData("don't stop now", "don't")]
    [InlineData("U.S.A today", "U.S.A")]
    [InlineData("1.5 times", "1.5")]
    [InlineData("café René", "café")]
    [InlineData("world 你好", "world")]
    public void NextChunk_SpaceDelimitedTextNeverEntersTheSpacelessBranch(string tail, string expected)
    {
        using var segmenter = new SpacelessSegmenter("h", "d", "U", "1", "c", "w");

        Assert.Equal(expected, AcceptanceChunker.NextChunk(tail));
    }

    [Theory]
    [InlineData("你好世界", "你好", "你好")]
    [InlineData("今日はいい天気です", "今日", "今日")]
    [InlineData("สวัสดีครับ", "สวัสดี", "สวัสดี")]
    [InlineData("你好 world", "你好", "你好")]
    [InlineData(" 你好世界", "你好", " 你好")]
    public void NextChunk_SpacelessRun_TakesOnlyTheFirstSegmentedWord(string tail, string firstWord, string expected)
    {
        using var segmenter = new SpacelessSegmenter(firstWord);

        Assert.Equal(expected, AcceptanceChunker.NextChunk(tail));
    }

    [Theory]
    [InlineData("・・・ あと", "・・・")]
    [InlineData("・・・", "・・・")]
    public void NextChunk_SpacelessRunWithNoSegmentedWord_TakesTheWholeToken(string tail, string expected)
    {
        using var segmenter = new SpacelessSegmenter();

        Assert.Equal(expected, AcceptanceChunker.NextChunk(tail));
    }

    [Theory]
    [InlineData("資料、内容", "資料、")]
    [InlineData("内容。「次", "内容。")]
    public void NextChunk_BindsTrailingCjkPunctuationToTheWordButStopsBeforeAnOpener(string tail, string expected) =>
        Assert.Equal(expected, AcceptanceChunker.NextChunk(tail));

    [Theory]
    [InlineData("、理解し、その内容", "、")]
    [InlineData("。」次の文", "。」")]
    [InlineData("「分かった」と言った", "「")]
    [InlineData("【内容】次", "【")]
    [InlineData("。」「次の文", "。」「")]
    public void NextChunk_PeelsALeadingCjkPunctuationRunAsItsOwnChunk(string tail, string expected) =>
        Assert.Equal(expected, AcceptanceChunker.NextChunk(tail));

    [Theory]
    [InlineData("(hello) world", "(hello)")]
    [InlineData("\"quote\" next", "\"quote\"")]
    public void NextChunk_AsciiBracketsAndQuotesStayWithTheirToken(string tail, string expected) =>
        Assert.Equal(expected, AcceptanceChunker.NextChunk(tail));

    [Theory]
    [InlineData("資料、内容", "資料")]
    [InlineData("、内容", "、")]
    public void NextChunk_PunctuationOff_PeelsCjkPunctuationOffTheWordButNeverReturnsEmpty(string tail, string expected) =>
        Assert.Equal(expected, AcceptanceChunker.NextChunk(tail, autoAcceptTrailingPunctuation: false));

    [Theory]
    [InlineData("你好世界", 4)]
    [InlineData("안녕하세요", 5)]
    [InlineData("สวัสดีครับ", 10)]
    [InlineData("テストです", 3)]
    [InlineData("これは本", 3)]
    [InlineData("資料、内容", 2)]
    [InlineData("你好world", 2)]
    public void ScriptRunWordEnd_EndsTheWordWhereTheScriptChanges(string run, int expected) =>
        Assert.Equal(expected, AcceptanceChunker.ScriptRunWordEnd(run, 0, run.Length));

    [Theory]
    [InlineData("食べる前", 3)]
    [InlineData("読み方", 2)]
    public void ScriptRunWordEnd_KeepsOneKanaEndingWithItsKanji(string run, int expected) =>
        Assert.Equal(expected, AcceptanceChunker.ScriptRunWordEnd(run, 0, run.Length));

    [Fact]
    public void ScriptRunWordEnd_StartsAtTheGivenOffsetAndStopsAtTheLimit() =>
        Assert.Equal(5, AcceptanceChunker.ScriptRunWordEnd("ab 你好世界", 3, 5));
}
