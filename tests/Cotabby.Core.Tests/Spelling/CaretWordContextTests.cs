using System.Globalization;
using Cotabby.Core.Spelling;

namespace Cotabby.Core.Tests.Spelling;

/// <summary>Boundaries come from typed characters, never elapsed time or dictionary membership.</summary>
public class CaretWordContextTests
{
    [Theory]
    [InlineData("because")]
    [InlineData("schedule")]
    [InlineData("recommend")]
    [InlineData("Celsius")]
    [InlineData("don't")]
    public void EveryPausedPrefixRemainsUncommitted(string word)
    {
        for (int count = 1; count <= word.Length; count++)
        {
            var text = "Please " + word[..count];
            Assert.Null(CaretWordContext.CommittedWordIn(text));
            Assert.Equal(word[..count], CaretWordContext.UnfinishedWord(text));
        }
    }

    [Theory]
    [InlineData(" ")]
    [InlineData(",")]
    [InlineData("!")]
    [InlineData("? ")]
    public void CommittedPunctuationIsPreservedOnAcceptance(string delimiter)
    {
        Assert.Equal(
            new CaretWordContext.CommittedWord("nmae", delimiter),
            CaretWordContext.CommittedWordIn("a nmae" + delimiter));
        Assert.Equal(
            "name" + delimiter,
            TypoCorrectionReplacementPlanner.Plan("a nmae" + delimiter, "nmae", "name", requiresTrailingSpace: false)?.ReplacementText);
    }

    [Fact]
    public void AnUncommittedWordHasNoReplacement()
    {
        Assert.Null(TypoCorrectionReplacementPlanner.Plan("a nmae", "nmae", "name", requiresTrailingSpace: false));
    }

    [Theory]
    [InlineData("call user_name")]
    [InlineData("open example.com")]
    [InlineData("version v2")]
    [InlineData("今日は")]
    [InlineData("word  ")]
    [InlineData("call(wor")]
    [InlineData("array[wor")]
    [InlineData("[wor")]
    [InlineData("{wor")]
    [InlineData("`wor")]
    [InlineData("\"user_name")]
    [InlineData("(example.com")]
    public void CodeAndUnspacedScriptsRemainOutsideLexicalPolicy(string text)
    {
        Assert.Null(CaretWordContext.UnfinishedWord(text));
        Assert.Null(CaretWordContext.CommittedWordIn(text));
    }

    [Theory]
    [InlineData("(")]
    [InlineData("\"")]
    [InlineData("'")]
    [InlineData("“")]
    [InlineData("‘")]
    [InlineData("«")]
    [InlineData("‹")]
    [InlineData("(“")]
    public void ProseOpenersKeepTheWordInsideTheLexicalPolicy(string opening)
    {
        Assert.Equal("wor", CaretWordContext.UnfinishedWord("Try " + opening + "wor"));
        Assert.Equal("don't", CaretWordContext.UnfinishedWord("Try " + opening + "don't"));
        Assert.Null(CaretWordContext.UnfinishedWord("Try " + opening));
    }

    /// <summary>Recognizing prose for generation does not expand the strict replacement planner's range.</summary>
    [Theory]
    [InlineData("(")]
    [InlineData("\"")]
    [InlineData("“")]
    public void OpeningPunctuationDoesNotBecomePartOfACorrection(string opening)
    {
        Assert.Null(CaretWordContext.CommittedWordIn("Try " + opening + "nmae "));
    }

    [Theory]
    [InlineData(";")]
    [InlineData(":")]
    [InlineData(", ")]
    [InlineData("; ")]
    public void CommittedWordDelimiters(string delimiter)
    {
        Assert.Equal(
            new CaretWordContext.CommittedWord("nmae", delimiter),
            CaretWordContext.CommittedWordIn("a nmae" + delimiter));
    }

    /// <summary>A period is not a delimiter because example.com and a sentence end look identical at this boundary.</summary>
    [Theory]
    [InlineData("a nmae.")]
    [InlineData("a nmae. ")]
    [InlineData("a nmae\t")]
    [InlineData("a nmae\n")]
    [InlineData("a nmae,,")]
    public void OtherEndingsDoNotCommitAWord(string text)
    {
        Assert.Null(CaretWordContext.CommittedWordIn(text));
    }

    [Theory]
    [InlineData("", null)]
    [InlineData("Hello ", null)]
    [InlineData("I like naïve", "naïve")]
    [InlineData("I don’t", "don’t")]
    // A trailing connector is still the word being typed.
    [InlineData("a well-", "well-")]
    [InlineData("code abc1", null)]
    // Hangul syllables are letters but the script is excluded as unspaced text.
    [InlineData("인사 안녕", null)]
    public void UnfinishedWordEdges(string text, string? expected)
    {
        Assert.Equal(expected, CaretWordContext.UnfinishedWord(text));
    }

    [Fact]
    public void DecomposedAccentsStayInsideTheWord()
    {
        // The combining mark belongs to the letter before it, so the word is still all letters.
        var word = "re\u0301sume\u0301";
        Assert.Equal(6, new StringInfo(word).LengthInTextElements);
        Assert.Equal(word, CaretWordContext.UnfinishedWord("my " + word));
        Assert.Equal(new CaretWordContext.CommittedWord(word, ", "), CaretWordContext.CommittedWordIn("my " + word + ", "));
    }

    [Fact]
    public void UnspacedScriptsOutsideTheBasicPlaneAreExcluded()
    {
        // U+20000 is a CJK ideograph encoded as a surrogate pair.
        Assert.Null(CaretWordContext.UnfinishedWord("see \U00020000\U00020001"));
        Assert.Equal("𝒶𝒷", CaretWordContext.UnfinishedWord("see 𝒶𝒷"));
    }
}
