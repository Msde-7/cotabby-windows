using Cotabby.Core.Support;

namespace Cotabby.Core.Tests;

public class PromptSectionBudgetTests
{
    private static PromptSection Section(
        string name, string content, int priority, int min = 0, int max = 10_000,
        SectionTruncation truncation = SectionTruncation.PreserveStart) =>
        new(name, content, priority, min, max, truncation);

    /// <summary>The live caret section. Whitespace is meaningful and it is kept by its end.</summary>
    private static PromptSection CaretSection(string content) =>
        new("prefix", content, 100, 1, 100, SectionTruncation.PreserveEnd, PreservesWhitespace: true);

    private static IEnumerable<string> Names(IEnumerable<PromptSection> sections) => sections.Select(s => s.Name);

    private static IEnumerable<string> Contents(IEnumerable<PromptSection> sections) => sections.Select(s => s.Content);

    [Fact]
    public void AmpleBudgetKeepsAllInInputOrder()
    {
        var kept = PromptSectionBudget.Allocate(
            [Section("first", "aa", priority: 1), Section("second", "bb", priority: 9)], totalChars: 1000);
        Assert.Equal(["first", "second"], Names(kept));
    }

    [Fact]
    public void DropsLowerPriorityWhenBudgetTight()
    {
        var kept = PromptSectionBudget.Allocate(
            [Section("low", "xxxxxxxx", priority: 1), Section("high", "yyyyyyyy", priority: 9)], totalChars: 8);
        Assert.Equal(["high"], Names(kept));
    }

    [Fact]
    public void EqualPriorityTieGoesToEarlierSection()
    {
        var kept = PromptSectionBudget.Allocate(
            [Section("a", "xxxx", priority: 5), Section("b", "yyyy", priority: 5)], totalChars: 4);
        Assert.Equal(["a"], Names(kept));
    }

    [Fact]
    public void TruncatesLowerPriorityToTheRemainder()
    {
        var kept = PromptSectionBudget.Allocate(
            [Section("a", new string('a', 100), priority: 9), Section("b", new string('b', 100), priority: 8)],
            totalChars: 120);
        Assert.Equal([new string('a', 100), new string('b', 20)], Contents(kept));
    }

    [Fact]
    public void MaxCharsCapsSectionInItsTruncationDirection()
    {
        var kept = PromptSectionBudget.Allocate(
            [
                Section("head", "abcdef", priority: 5, max: 3, truncation: SectionTruncation.PreserveStart),
                Section("tail", "abcdef", priority: 5, max: 3, truncation: SectionTruncation.PreserveEnd),
            ],
            totalChars: 1000);
        Assert.Equal(["abc", "def"], Contents(kept));
    }

    [Fact]
    public void MinCharsBoundary()
    {
        var candidate = Section("s", "xxxxx", priority: 5, min: 5);
        Assert.Equal(["s"], Names(PromptSectionBudget.Allocate([candidate], totalChars: 5)));
        Assert.Empty(PromptSectionBudget.Allocate([candidate], totalChars: 4));
    }

    [Fact]
    public void DroppedMinCharsSectionLeavesBudgetForOthers()
    {
        var kept = PromptSectionBudget.Allocate(
            [Section("big", new string('x', 50), priority: 9, min: 30), Section("small", "ok", priority: 1)],
            totalChars: 20);
        Assert.Equal(["small"], Names(kept));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void NonPositiveBudgetKeepsNothing(int budget)
    {
        Assert.Empty(PromptSectionBudget.Allocate([Section("a", "alpha", priority: 9)], totalChars: budget));
    }

    [Fact]
    public void DropsWhitespaceOnlyContent()
    {
        var kept = PromptSectionBudget.Allocate(
            [Section("blank", "   ", priority: 9), Section("real", "hello", priority: 8)], totalChars: 1000);
        Assert.Equal(["real"], Names(kept));
    }

    [Fact]
    public void ChargesOnlyTrimmedContentForReferenceSections()
    {
        var kept = PromptSectionBudget.Allocate(
            [Section("a", " x ", priority: 9), Section("b", "yyy", priority: 5)], totalChars: 4);
        Assert.Equal(["x", "yyy"], Contents(kept));
    }

    [Fact]
    public void CountsPreservedCaretWhitespaceAgainstTheBudget()
    {
        var prefix = CaretSection("Hello \n  ");
        var kept = PromptSectionBudget.Allocate(
            [prefix, Section("notes", "extra", priority: 10)], totalChars: prefix.Content.Length);
        Assert.Equal([prefix], kept);
    }

    [Fact]
    public void PreservesCaretSideWhitespaceWhenTruncating()
    {
        Assert.Equal(["\n  "], Contents(PromptSectionBudget.Allocate([CaretSection("Hello \n  ")], totalChars: 3)));
    }

    [Theory]
    [InlineData("abcdefgh", 3, SectionTruncation.PreserveEnd, "fgh")]
    [InlineData("abcdefgh", 3, SectionTruncation.PreserveStart, "abc")]
    [InlineData("abc", 10, SectionTruncation.PreserveEnd, "abc")]
    [InlineData("abc", 3, SectionTruncation.PreserveStart, "abc")]
    [InlineData("abc", 0, SectionTruncation.PreserveStart, "")]
    [InlineData("abc", -1, SectionTruncation.PreserveEnd, "")]
    // A surrogate pair is one character and is never split.
    [InlineData("a🎉b", 2, SectionTruncation.PreserveEnd, "🎉b")]
    public void TruncateKeepsRequestedEndOrReturnsInputUnchanged(
        string text, int chars, SectionTruncation mode, string expected)
    {
        Assert.Equal(expected, PromptSectionBudget.Truncate(text, chars, mode));
    }

    [Fact]
    public void TokenAllocateKeepsAllWhenBudgetAmple()
    {
        var kept = PromptSectionBudget.Allocate(
            [Section("a", "alpha", priority: 10), Section("b", "beta", priority: 5)],
            totalTokens: 1000, TokenCountEstimator.Estimate);
        Assert.Equal(["a", "b"], Names(kept));
    }

    [Fact]
    public void TokenAllocateDropsLowerPriorityWhenBudgetTight()
    {
        var kept = PromptSectionBudget.Allocate(
            [
                Section("low", "word word word word word ", priority: 1),
                Section("high", "term term term term term ", priority: 9),
            ],
            totalTokens: 5, TokenCountEstimator.Estimate);
        Assert.Equal(["high"], Names(kept));
    }

    [Fact]
    public void TokenAllocateTruncatesToRemainingTokensAndMaxChars()
    {
        // One character per token maps the token cap directly to characters. Fill order is a (4,
        // leaving 5), capped (2 by its maximum, leaving 3), then b (3 of 6).
        var kept = PromptSectionBudget.Allocate(
            [
                Section("a", "aaaa", priority: 9),
                Section("b", "bbbbbb", priority: 5),
                Section("capped", "cccccc", priority: 7, max: 2),
            ],
            totalTokens: 9, text => text.Length);
        Assert.Equal(["aaaa", "bbb", "cc"], Contents(kept));
    }

    [Fact]
    public void TokenAllocateDropsWhitespaceOnlyContent()
    {
        var kept = PromptSectionBudget.Allocate(
            [Section("blank", "   ", priority: 9), Section("real", "hello", priority: 8)],
            totalTokens: 100, TokenCountEstimator.Estimate);
        Assert.Equal(["real"], Names(kept));
    }

    [Fact]
    public void TokenAllocatePreservesWhitespaceAndChargesItsEstimate()
    {
        var prefix = CaretSection("word  \n\t");
        var kept = PromptSectionBudget.Allocate(
            [prefix, Section("notes", "extra", priority: 10)],
            totalTokens: prefix.Content.Length, text => text.Length);
        Assert.Equal([prefix], kept);
    }
}
