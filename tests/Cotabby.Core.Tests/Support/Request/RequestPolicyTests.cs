using Cotabby.Core.Support;

namespace Cotabby.Core.Tests;

public class CaretTokenPositionTests
{
    [Theory]
    [InlineData("Packing list: passport, charger, head", "phones, sunscreen")]
    [InlineData("The meeting is at 3", ":30 tomorrow")]
    [InlineData("Please send the report to jane", "@example.com before")]
    [InlineData("const total = items.red", "uce((sum, item)")]
    [InlineData("Our office is located at 41", "5 Mission Street")]
    [InlineData("snake", "_case")]
    public void CaretInsideWordsNumbersAndAddressesIsInsideToken(string precedingText, string trailingText)
    {
        Assert.True(CaretTokenPosition.IsInsideToken(precedingText, trailingText));
    }

    [Theory]
    [InlineData("Thanks for your patience", " while we sorted")]
    [InlineData("Thanks", ".")]
    [InlineData("Thanks", ". Next sentence")]
    [InlineData("Thanks", "")]
    [InlineData("Hello ", "world")]
    [InlineData("at 3", ": later")]
    public void CaretAtWordEndOrBeforePunctuationIsNotInsideToken(string precedingText, string trailingText)
    {
        Assert.False(CaretTokenPosition.IsInsideToken(precedingText, trailingText));
    }
}

/// <summary>Each tier is pinned at both edges so a threshold edit is always deliberate.</summary>
public class DebouncePolicyTests
{
    [Theory]
    // The configured fallback applies until a real (positive) latency exists.
    [InlineData(null, 20)]
    [InlineData(0, 20)]
    [InlineData(-5, 20)]
    [InlineData(1, 15)]
    [InlineData(70, 15)]
    [InlineData(71, 25)]
    [InlineData(140, 25)]
    [InlineData(141, 55)]
    [InlineData(900, 55)]
    public void LocalEngineTiersAndFallback(int? latency, int expected)
    {
        Assert.Equal(expected, DebouncePolicy.Milliseconds(latency, fallback: 20));
    }

    [Theory]
    [InlineData(null, 20, 180)]
    [InlineData(0, 20, 180)]
    [InlineData(-5, 20, 180)]
    [InlineData(1, 20, 100)]
    [InlineData(300, 20, 100)]
    [InlineData(301, 20, 150)]
    [InlineData(700, 20, 150)]
    [InlineData(701, 20, 220)]
    [InlineData(1_000, 20, 220)]
    // Without a latency, a configured fallback above the 180 ms floor wins.
    [InlineData(null, 250, 250)]
    public void EndpointTiersAndFallbackFloor(int? latency, int fallback, int expected)
    {
        Assert.Equal(expected, DebouncePolicy.Milliseconds(latency, fallback, isRemoteEndpoint: true));
    }
}

public class MidWordContinuationPolicyTests
{
    [Theory]
    [InlineData("I am wri", "ting", true)]
    // Nothing after the caret is a normal word boundary, where next-word predictions belong.
    [InlineData("The quick brown fox", "", false)]
    [InlineData("hello ", "world", false)]
    [InlineData("done", ". Next", false)]
    // Digits count as word characters, so identifiers and numbers heal like words.
    [InlineData("build 12", "34", true)]
    [InlineData("v", "2", true)]
    [InlineData("caf", "é au lait", true)]
    [InlineData("今日", "は", true)]
    [InlineData("", "word", false)]
    // A connector is not a word character, so the trigger stays narrow at "don|'t".
    [InlineData("don", "'t", false)]
    [InlineData("word", "\nnext", false)]
    public void ForcesContinuationOnlyInsideAWord(string precedingText, string trailingText, bool expected)
    {
        Assert.Equal(expected, MidWordContinuationPolicy.ShouldForceContinuation(precedingText, trailingText));
    }
}

public class SentenceBoundaryClassifierTests
{
    [Theory]
    [InlineData("I went home.", true)]
    [InlineData("I have a cat.", true)]
    [InlineData("pi is 3.14", false)]
    [InlineData("item 1.", false)]
    [InlineData("I visited the U.S.", false)]
    [InlineData("tabs and so on etc.", false)]
    // A period after a closing quote has no word to qualify it.
    [InlineData("He said \"stop\".", true)]
    public void LastPeriodIsTerminal(string text, bool expected)
    {
        Assert.Equal(expected, SentenceBoundaryClassifier.IsTerminalPeriod(text, text.LastIndexOf('.')));
    }

    [Fact]
    public void LeadingPeriodWithNothingBeforeIsTerminal()
    {
        Assert.True(SentenceBoundaryClassifier.IsTerminalPeriod(".", 0));
        Assert.True(SentenceBoundaryClassifier.EndsSentence("."));
    }

    [Fact]
    public void InitialInsideTextIsNotTerminal()
    {
        const string text = "The U.S. is large";
        Assert.False(SentenceBoundaryClassifier.IsTerminalPeriod(text, text.IndexOf('.')));
    }

    [Theory]
    [InlineData("Hello world.")]
    [InlineData("Yes!")]
    [InlineData("Really?")]
    [InlineData("Done.   ")]
    [InlineData("He said \"stop.\"")]
    [InlineData("(done!)")]
    // CJK terminators are unambiguous, and their closers are walked past like quotes.
    [InlineData("資料を読む。")]
    [InlineData("すごい！")]
    [InlineData("いいですか？")]
    [InlineData("終わり。」")]
    [InlineData("終わり｡")]
    [InlineData("終わり｡｣")]
    [InlineData("He said ‘done.’")]
    [InlineData("“Stop.”")]
    [InlineData("[finished!]")]
    [InlineData("{ok?}")]
    [InlineData("(see note.)  ")]
    public void EndsSentence(string text)
    {
        Assert.True(SentenceBoundaryClassifier.EndsSentence(text));
    }

    [Theory]
    [InlineData("Hello world")]
    [InlineData("It is version 1.")]
    [InlineData("for example, e.g.")]
    [InlineData("from the U.S.")]
    [InlineData("Hello Mr.")]
    [InlineData("")]
    // The ideographic comma is a clause boundary, not a sentence end.
    [InlineData("資料を読み、")]
    // Abbreviation matching ignores case.
    [InlineData("Meet Dr.")]
    [InlineData("See FIG.")]
    [InlineData("Apples, pears, ETC.")]
    [InlineData("Acme Inc.")]
    [InlineData("   \n")]
    [InlineData("\")]")]
    public void DoesNotEndSentence(string text)
    {
        Assert.False(SentenceBoundaryClassifier.EndsSentence(text));
    }
}
