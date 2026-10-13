using Cotabby.Core.Spelling;

namespace Cotabby.Core.Tests.Spelling;

public class CurrentWordExtractorTests
{
    [Fact]
    public void ExtractsTrailingWordAtCaret()
    {
        Assert.Equal("nmae", CurrentWordExtractor.Extract("hi my nmae"));
    }

    [Fact]
    public void ReturnsNullWhenCaretFollowsWhitespace()
    {
        Assert.Null(CurrentWordExtractor.Extract("hi my nmae "));
    }

    [Fact]
    public void ReturnsNullForEmptyText()
    {
        Assert.Null(CurrentWordExtractor.Extract(""));
    }

    [Fact]
    public void ReturnsNullForSingleCharacterWord()
    {
        Assert.Null(CurrentWordExtractor.Extract("a"));
        Assert.Null(CurrentWordExtractor.Extract("I am a"));
    }

    [Fact]
    public void RejectsAllCapsAcronyms()
    {
        Assert.Null(CurrentWordExtractor.Extract("ship via HTTP"));
        Assert.Null(CurrentWordExtractor.Extract("parse JSON"));
    }

    [Fact]
    public void RejectsTokensWithDigits()
    {
        Assert.Null(CurrentWordExtractor.Extract("build v2"));
        Assert.Null(CurrentWordExtractor.Extract("room 101a"));
    }

    [Fact]
    public void RejectsCodeLikeTokens()
    {
        Assert.Null(CurrentWordExtractor.Extract("open https://example.com"));
        Assert.Null(CurrentWordExtractor.Extract("call user_name"));
        Assert.Null(CurrentWordExtractor.Extract("ping @jacob"));
        Assert.Null(CurrentWordExtractor.Extract("the file.swift"));
    }

    [Fact]
    public void AcceptsMixedCaseNaturalWord()
    {
        Assert.Equal("teh", CurrentWordExtractor.Extract("fix teh"));
        // Leading-capital natural words are fine. Only all-caps tokens are rejected.
        Assert.Equal("Teh", CurrentWordExtractor.Extract("say Teh"));
    }

    [Fact]
    public void SurrogatePairsAndEmojiSequencesCountOnce()
    {
        // 𝒶 is one scalar in two UTF-16 units. The family emoji is five scalars in eight units.
        Assert.Equal("h𝒶t", CurrentWordExtractor.Extract("a h𝒶t"));
        Assert.Equal("👨\u200D👩\u200D👧x", CurrentWordExtractor.Extract("ok 👨\u200D👩\u200D👧x"));
        // A lone emoji is a single character, which is too short to act on.
        Assert.Null(CurrentWordExtractor.Extract("ok 👨\u200D👩\u200D👧"));
    }

    [Fact]
    public void LettersAreClassifiedByTheFirstScalarOfEachGrapheme()
    {
        // The combining accent is not a letter on its own, but its grapheme is.
        Assert.Null(CurrentWordExtractor.Extract("the E\u0301COLE"));
        Assert.Equal("E\u0301cole", CurrentWordExtractor.Extract("the E\u0301cole"));
        // Digits outside ASCII are numbers too.
        Assert.Null(CurrentWordExtractor.Extract("room ab٣"));
    }

    [Fact]
    public void WordBoundaryIsAnyWhitespace()
    {
        Assert.Equal("teh", CurrentWordExtractor.Extract("line one\nteh"));
        Assert.Equal("teh", CurrentWordExtractor.Extract("col\tteh"));
        Assert.Equal("teh", CurrentWordExtractor.Extract("non\u00A0teh"));
        Assert.Equal("teh", CurrentWordExtractor.Extract("teh"));
    }

    [Fact]
    public void ApostrophesHyphensAndTrailingProsePunctuationStayInTheToken()
    {
        Assert.Equal("didn't", CurrentWordExtractor.Extract("I didn't"));
        Assert.Equal("well-knwon", CurrentWordExtractor.Extract("a well-knwon"));
        // Trailing prose punctuation is not stripped here. The spell checker rejects it downstream.
        Assert.Equal("nmae,", CurrentWordExtractor.Extract("hi nmae,"));
    }

    [Fact]
    public void TypoReplacement_PreservesCommittedSpaceAndUsesUtf16Count()
    {
        Assert.Equal(
            new TypoCorrectionReplacement(5, "name "),
            TypoCorrectionReplacementPlanner.Plan("hi my nmae ", "nmae", "name", requiresTrailingSpace: true));
    }

    [Fact]
    public void TypoReplacement_CountsUtf16UnitsForDecomposedAndAstralText()
    {
        // The typo is four graphemes but six UTF-16 units, and the inserter deletes by unit.
        var typo = "ca𝒻e\u0301";
        Assert.Equal(
            new TypoCorrectionReplacement(7, "café "),
            TypoCorrectionReplacementPlanner.Plan("un " + typo + " ", typo, "café", requiresTrailingSpace: true));
    }

    [Fact]
    public void TypoReplacement_RejectsAutomaticFixBeforeSpace()
    {
        Assert.Null(TypoCorrectionReplacementPlanner.Plan("hi my nmae", "nmae", "name", requiresTrailingSpace: true));
    }

    [Fact]
    public void TypoReplacement_PreservesPunctuationDelimiterWhenSpaceIsNotRequired()
    {
        Assert.Equal(
            new TypoCorrectionReplacement(6, "name, "),
            TypoCorrectionReplacementPlanner.Plan("hi my nmae, ", "nmae", "name", requiresTrailingSpace: false));
        // Automatic fixes only fire on a bare trailing space, so a comma and a space is refused there.
        Assert.Null(TypoCorrectionReplacementPlanner.Plan("hi my nmae, ", "nmae", "name", requiresTrailingSpace: true));
    }

    [Fact]
    public void TypoReplacement_TrimsTheCorrection()
    {
        Assert.Equal(
            new TypoCorrectionReplacement(5, "name "),
            TypoCorrectionReplacementPlanner.Plan("hi my nmae ", "nmae", "  name\n", requiresTrailingSpace: true));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("nmae")]
    public void TypoReplacement_RejectsBlankOrNoOpCorrections(string correction)
    {
        Assert.Null(TypoCorrectionReplacementPlanner.Plan("hi my nmae ", "nmae", correction, requiresTrailingSpace: false));
    }

    [Fact]
    public void TypoReplacement_RejectsChangedTrailingWord()
    {
        Assert.Null(TypoCorrectionReplacementPlanner.Plan("hi my names ", "nmae", "name", requiresTrailingSpace: false));
    }
}
