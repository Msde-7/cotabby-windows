using System.Text;
using Cotabby.Core.Support;

namespace Cotabby.Core.Tests;

/// <summary>Editor-byte and work-budget invariants, independent of any model vocabulary.</summary>
public class TokenHealingPlanTests
{
    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    /// <summary>Token i decodes to the i-th piece.</summary>
    private static TokenHealingPlan Plan(string prompt, string[] pieces, bool singleLine = false) =>
        new(prompt, Enumerable.Range(0, pieces.Length).ToArray(), singleLine, token => Utf8(pieces[token]));

    [Fact]
    public void PartialWordSpanningTokensCanChooseAWholeWordToken()
    {
        var plan = Plan("apple intell", ["", "apple", " int", "ell"]);
        Assert.Equal([0, 1], plan.PromptTokens);
        Assert.Equal(" intell", Encoding.UTF8.GetString(plan.ReplayBytes));
        Assert.Equal("igence", new TokenHealingBuffer(plan.ReplayBytes).Append(Utf8(" intelligence")));
    }

    [Fact]
    public void AlreadyCompletedWordCanReplayExactlyBeforeAddingSpace()
    {
        var plan = Plan("apple intelligence", ["", "apple", " int", "elligence"]);
        Assert.Equal([0, 1], plan.PromptTokens);
        var buffer = new TokenHealingBuffer(plan.ReplayBytes);
        Assert.Null(buffer.Append(Utf8(" intelligence")));
        Assert.Equal(" is", buffer.Append(Utf8(" is")));
    }

    [Fact]
    public void UnhealedPlanKeepsTheExactPromptSoMidWordMaskingApplies()
    {
        int[] tokens = [0, 1, 2, 3];
        var plan = TokenHealingPlan.Unhealed(tokens);
        Assert.Equal(tokens, plan.PromptTokens);
        Assert.Empty(plan.ReplayBytes);
    }

    [Fact]
    public void TrailingSpaceDoesNotReconsiderThePreviousWord()
    {
        var plan = Plan("apple ", ["", "apple", " "]);
        Assert.Equal([0, 1], plan.PromptTokens);
        Assert.Equal(Utf8(" "), plan.ReplayBytes);
    }

    [Fact]
    public void UnicodeScalarSplitAcrossTokensReplaysExactBytes()
    {
        byte[][] pieces = [[], Utf8("Try"), [0x20, 0xC3], [0xA9]];
        var plan = new TokenHealingPlan("Try é", [0, 1, 2, 3], singleLine: false, token => pieces[token]);
        Assert.Equal([0, 1], plan.PromptTokens);
        Assert.Equal(Utf8(" é"), plan.ReplayBytes);
        Assert.Equal("clair", new TokenHealingBuffer(plan.ReplayBytes).Append(Utf8(" éclair")));
    }

    [Fact]
    public void LongIdentifierRetainsBoundedLastTokenFallback()
    {
        var plan = Plan("a extraordinarilylongidentifier", ["", "a", " extraordinarilylong", "identifier"]);
        Assert.Equal([0, 1, 2], plan.PromptTokens);
        Assert.Equal(Utf8("identifier"), plan.ReplayBytes);
    }

    [Fact]
    public void TokenOvershootingBudgetDoesNotRemoveThePreviousWord()
    {
        var plan = Plan("extraordinarilylong intell", ["", "extraordinarilylong int", "ell"]);
        Assert.Equal([0, 1], plan.PromptTokens);
        Assert.Equal(Utf8("ell"), plan.ReplayBytes);
    }

    [Fact]
    public void SingleLineReplayCannotCrossANewlineInsideAToken()
    {
        var plan = Plan("title\nintell", ["", "title", "\nint", "ell"], singleLine: true);
        Assert.Equal([0, 1, 2], plan.PromptTokens);
        Assert.Equal(Utf8("ell"), plan.ReplayBytes);
    }

    [Fact]
    public void NewlineRemainsConditioningWhenTokenBoundaryAllowsIt()
    {
        var plan = Plan("title\nintell", ["", "title", "\n", "int", "ell"], singleLine: true);
        Assert.Equal([0, 1, 2], plan.PromptTokens);
        Assert.Equal(Utf8("intell"), plan.ReplayBytes);
    }

    [Fact]
    public void TokenizerNormalizationCannotRewriteTheEditorsBytes()
    {
        var plan = Plan("Try é", ["", "Try", " e\u0301"]);
        Assert.Equal([0, 1, 2], plan.PromptTokens);
        Assert.Empty(plan.ReplayBytes);
    }

    // Each row isolates one guard on removing the final prompt token.
    [Theory]
    // 18 bytes exceeds the healed byte limit of 16, so nothing is removed.
    [InlineData("x abcdefghijklmnopq", new[] { "", "x", " abcdefghijklmnopq" }, false, 3, "")]
    // An empty (special) piece cannot be replayed byte for byte.
    [InlineData("ab", new[] { "", "ab", "" }, false, 3, "")]
    // Single-line fields never replay a line break. Multi-line fields may.
    [InlineData("a\n", new[] { "", "a", "\n" }, true, 3, "")]
    [InlineData("a\r", new[] { "", "a", "\r" }, true, 3, "")]
    [InlineData("a\n", new[] { "", "a", "\n" }, false, 2, "\n")]
    // Punctuation at the caret heals only that token, not the word before it.
    [InlineData("hello,", new[] { "", "hello", "," }, false, 2, ",")]
    // Exactly at the byte limit still heals.
    [InlineData("x abcdefghijklmno", new[] { "", "x", " abcdefghijklmno" }, false, 2, " abcdefghijklmno")]
    public void LastTokenHealingGuards(string prompt, string[] pieces, bool singleLine, int keptCount, string replay)
    {
        var plan = Plan(prompt, pieces, singleLine);
        Assert.Equal(Enumerable.Range(0, keptCount), plan.PromptTokens);
        Assert.Equal(Utf8(replay), plan.ReplayBytes);
    }

    [Fact]
    public void AtLeastOneConditioningTokenSurvivesWithoutBos()
    {
        var plan = Plan("intell", ["int", "ell"]);
        Assert.Equal([0], plan.PromptTokens);
        Assert.Equal(Utf8("ell"), plan.ReplayBytes);
        var single = Plan("int", ["int"]);
        Assert.Equal([0], single.PromptTokens);
        Assert.Empty(single.ReplayBytes);
    }
}
