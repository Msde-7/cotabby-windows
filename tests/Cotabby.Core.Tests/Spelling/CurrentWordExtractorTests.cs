using Cotabby.Core.Spelling;

namespace Cotabby.Core.Tests.Spelling;

public class CurrentWordExtractorTests
{
    [Fact]
    public void ExtractsTrailingWordAtCaret()
    {
        Assert.Equal(new CurrentWordExtractor.Result("nmae", 4), CurrentWordExtractor.Extract("hi my nmae"));
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
        Assert.Equal("teh", CurrentWordExtractor.Extract("fix teh")?.Word);
        // Leading-capital natural words are fine. Only all-caps tokens are rejected.
        Assert.Equal("Teh", CurrentWordExtractor.Extract("say Teh")?.Word);
    }

    [Fact]
    public void CharacterCountIsGraphemeCount()
    {
        // A decomposed é is two UTF-16 units but one grapheme, and one Backspace removes it.
        var word = "cafe\u0301";
        var result = CurrentWordExtractor.Extract("a " + word);
        Assert.Equal(word, result?.Word);
        Assert.Equal(4, result?.CharacterCount);
        Assert.Equal(5, word.Length);
    }

    [Fact]
    public void SurrogatePairsAndEmojiSequencesCountOnce()
    {
        // 𝒶 is one scalar in two UTF-16 units. The family emoji is five scalars in eight units.
        Assert.Equal(new CurrentWordExtractor.Result("h𝒶t", 3), CurrentWordExtractor.Extract("a h𝒶t"));
        Assert.Equal(2, CurrentWordExtractor.Extract("ok 👨\u200D👩\u200D👧x")?.CharacterCount);
        // A lone emoji is a single character, which is too short to act on.
        Assert.Null(CurrentWordExtractor.Extract("ok 👨\u200D👩\u200D👧"));
    }

    [Fact]
    public void LettersAreClassifiedByTheFirstScalarOfEachGrapheme()
    {
        // The combining accent is not a letter on its own, but its grapheme is.
        Assert.Null(CurrentWordExtractor.Extract("the E\u0301COLE"));
        Assert.Equal("E\u0301cole", CurrentWordExtractor.Extract("the E\u0301cole")?.Word);
        // Digits outside ASCII are numbers too.
        Assert.Null(CurrentWordExtractor.Extract("room ab٣"));
    }

    [Fact]
    public void WordBoundaryIsAnyWhitespace()
    {
        Assert.Equal("teh", CurrentWordExtractor.Extract("line one\nteh")?.Word);
        Assert.Equal("teh", CurrentWordExtractor.Extract("col\tteh")?.Word);
        Assert.Equal("teh", CurrentWordExtractor.Extract("non\u00A0teh")?.Word);
        Assert.Equal("teh", CurrentWordExtractor.Extract("teh")?.Word);
    }

    [Fact]
    public void ApostrophesHyphensAndTrailingProsePunctuationStayInTheToken()
    {
        Assert.Equal("didn't", CurrentWordExtractor.Extract("I didn't")?.Word);
        Assert.Equal("well-knwon", CurrentWordExtractor.Extract("a well-knwon")?.Word);
        // Trailing prose punctuation is not stripped here. The spell checker rejects it downstream.
        Assert.Equal("nmae,", CurrentWordExtractor.Extract("hi nmae,")?.Word);
    }

    [Fact]
    public void TrailingWord_NoSpaceMatchesStrictExtraction()
    {
        var extracted = CurrentWordExtractor.ExtractTrailingWord("hi my nmae");
        Assert.Equal("nmae", extracted?.Result.Word);
        Assert.Equal(0, extracted?.TrailingSpaceCount);
    }

    [Fact]
    public void TrailingWord_ToleratesOneTrailingSpace()
    {
        var extracted = CurrentWordExtractor.ExtractTrailingWord("hi my nmae ");
        Assert.Equal("nmae", extracted?.Result.Word);
        Assert.Equal(1, extracted?.TrailingSpaceCount);
    }

    [Fact]
    public void TrailingWord_RejectsTwoTrailingSpaces()
    {
        Assert.Null(CurrentWordExtractor.ExtractTrailingWord("hi my nmae  "));
    }

    [Fact]
    public void TrailingWord_RejectsTrailingTabOrNewline()
    {
        Assert.Null(CurrentWordExtractor.ExtractTrailingWord("hi my nmae\t"));
        Assert.Null(CurrentWordExtractor.ExtractTrailingWord("hi my nmae\n"));
        Assert.Null(CurrentWordExtractor.ExtractTrailingWord("hi my nmae\t "));
    }

    [Fact]
    public void TrailingWord_RejectsImplausibleWordEvenWithSpace()
    {
        Assert.Null(CurrentWordExtractor.ExtractTrailingWord("open https://example.com "));
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
