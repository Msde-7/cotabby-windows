using Cotabby.Core.Support;
using Xunit;

namespace Cotabby.Core.Tests;

/// <summary>
/// Guards against degenerate output in the ported pipeline: the token-level
/// no-repeat-3-gram guard and the one-shot normalizer replace the old
/// char-run string heuristics.
/// </summary>
public class DegenerationGuardSmokeTests
{
    // ---- RepetitionGuard ---------------------------------------------------

    [Fact]
    public void RepetitionGuard_BlocksTokenThatWouldRepeatTrigram()
    {
        // History: [1,2,3,1,2] — prefix (1,2) previously continued with 3.
        var blocked = RepetitionGuard.BlockedTokens([1, 2, 3, 1, 2]);
        Assert.Contains(3, blocked);
    }

    [Fact]
    public void RepetitionGuard_NoBlocksOnShortHistory()
    {
        Assert.Empty(RepetitionGuard.BlockedTokens([7]));
    }

    [Fact]
    public void RepetitionGuard_DifferentPrefixNotBlocked()
    {
        var blocked = RepetitionGuard.BlockedTokens([1, 2, 3, 4, 5]);
        Assert.Empty(blocked);
    }

    // ---- Normalizer: chat-scaffolding & degenerate output -------------------

    [Fact]
    public void Normalizer_StripsThinkBlocks()
    {
        var r = SuggestionTextNormalizer.Normalize(
            "<think>reasoning here</think> the actual continuation",
            prompt: "p", precedingText: "Hello ", trailingText: "", singleLine: true);
        Assert.Equal("the actual continuation", r.Text);
    }

    [Fact]
    public void Normalizer_StripsScaffoldingLabels()
    {
        var r = SuggestionTextNormalizer.Normalize(
            "Continuation: rest of the sentence",
            prompt: "p", precedingText: "Some text", trailingText: "", singleLine: true);
        Assert.Equal("rest of the sentence", r.Text.Trim());
    }

    [Fact]
    public void Normalizer_SingleLineCutsAtFirstNewline()
    {
        var r = SuggestionTextNormalizer.Normalize(
            "first line\nsecond line",
            prompt: "p", precedingText: "x", trailingText: "", singleLine: true);
        Assert.Equal("first line", r.Text);
    }

    [Fact]
    public void Normalizer_MultiLineCutsAtBlankLine()
    {
        var r = SuggestionTextNormalizer.Normalize(
            "para one line one\npara one line two\n\npara two",
            prompt: "unrelated prompt", precedingText: "x", trailingText: "", singleLine: false);
        Assert.Equal("para one line one\npara one line two", r.Text);
    }

    [Fact]
    public void Normalizer_SuppressesTrailingDuplication()
    {
        var r = SuggestionTextNormalizer.Normalize(
            "world peace",
            prompt: "p", precedingText: "hello ", trailingText: "world peace now", singleLine: true);
        Assert.True(r.IsSuppressed);
        Assert.Equal(SuppressionReason.DuplicatesTrailingText, r.Reason);
    }

    [Fact]
    public void Normalizer_StripsEchoOfPrecedingWords()
    {
        var r = SuggestionTextNormalizer.Normalize(
            "quick brown fox jumps high",
            prompt: "p", precedingText: "the quick brown fox", trailingText: "", singleLine: true);
        Assert.Equal("jumps high", r.Text);
    }

    [Fact]
    public void Normalizer_RejectsReplacementCharacter()
    {
        var r = SuggestionTextNormalizer.Normalize(
            "abc�def",
            prompt: "p", precedingText: "x", trailingText: "", singleLine: true);
        Assert.True(r.IsSuppressed);
    }

    [Fact]
    public void Normalizer_DropsLeadingSpaceAfterTrailingWhitespace()
    {
        var r = SuggestionTextNormalizer.Normalize(
            " next words",
            prompt: "p", precedingText: "typed ", trailingText: "", singleLine: true);
        Assert.Equal("next words", r.Text);
    }

    // ---- Sentence boundary --------------------------------------------------

    [Theory]
    [InlineData("This is done.", true)]
    [InlineData("Really?", true)]
    [InlineData("Version 3.", false)]  // digit before period
    [InlineData("Dr.", false)]         // abbreviation
    [InlineData("J.", false)]          // single-letter initial
    [InlineData("no terminator", false)]
    public void SentenceBoundary_Classifies(string text, bool expected)
    {
        Assert.Equal(expected, SentenceBoundaryClassifier.EndsSentence(text));
    }
}
