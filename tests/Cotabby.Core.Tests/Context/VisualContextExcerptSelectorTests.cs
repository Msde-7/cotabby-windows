using Cotabby.Core.Context;

namespace Cotabby.Core.Tests.Context;

/// <summary>
/// Lines near the focused field, in its column first, win the character budget. OCR without
/// geometry favors the latest lines. The chosen lines come back in reading order with numbers and
/// names intact.
/// </summary>
/// <remarks>
/// The fixtures are upstream's Vision rectangles, whose origin is at the bottom left. <see cref="OcrBounds"/>
/// puts the origin at the top left, so each rectangle is flipped with <see cref="FromBottomLeft"/>,
/// which keeps every vertical distance and so every ranking the same.
/// </remarks>
public sealed class VisualContextExcerptSelectorTests
{
    private static readonly OcrBounds Focus = FromBottomLeft(0.4, 0.1, 0.4, 0.1);

    private static OcrBounds FromBottomLeft(double x, double y, double width, double height) =>
        new(x, 1 - y - height, width, height);

    private static OcrLine Line(string text, double x, double y) => new(text, 1f, FromBottomLeft(x, y, 0.25, 0.03));

    private static OcrLine[] Lines(params string[] texts) => texts.Select(t => new OcrLine(t, 1f)).ToArray();

    private static string Select(IReadOnlyList<OcrLine> lines, int budget) =>
        VisualContextExcerptSelector.Select(lines, "", Focus, budget);

    [Fact]
    public void PreservesNamesDeadlinesAndAmountsAfterConfidenceFiltering()
    {
        const string text = "Casey asks for 12 copies by September 25 for 450 dollars";
        Assert.Equal(text, Select([new OcrLine(text, 0.95f)], budget: 4000));
    }

    [Fact]
    public void KeepsNearbyConversationBeforeDistantSidebarAndRestoresReadingOrder()
    {
        OcrLine[] lines =
        [
            Line("The earlier message explains the project", x: 0.4, y: 0.5),
            Line("The newest message asks about the deadline", x: 0.4, y: 0.3),
            Line("The unrelated sidebar lists another project", x: 0, y: 0.25),
        ];
        Assert.Equal(lines[0].Text + "\n" + lines[1].Text, Select(lines, budget: 90));
    }

    [Fact]
    public void DropsFieldEchoesDuplicatesAndLowConfidence()
    {
        OcrLine[] lines =
        [
            Line("My current draft", x: 0.4, y: 0.2),
            Line("Please send the project agenda", x: 0.4, y: 0.3),
            Line("Please send the project agenda", x: 0.4, y: 0.4),
            new("unreliable recognition", 0.1f),
        ];
        Assert.Equal(
            "Please send the project agenda",
            VisualContextExcerptSelector.Select(lines, "My current draft", focusBounds: null, maxCharacters: 4000));
    }

    [Fact]
    public void WithoutGeometryPrefersRecentLinesAndHonorsBudget()
    {
        var lines = Lines("Old conversation", "New conversation");
        Assert.Equal("New conversation", VisualContextExcerptSelector.Select(lines, "", focusBounds: null, maxCharacters: 16));
        Assert.Equal("", Select(lines, budget: 0));
    }

    [Fact]
    public void RetainsMoreThanOldFortyLineLimitWhenBudgetAllows()
    {
        var lines = Enumerable.Range(0, 80).Select(i => new OcrLine($"Project agenda item {i}", 1f)).ToList();
        string result = Select(lines, budget: 4000);
        Assert.Equal(80, result.Split('\n').Length);
        Assert.True(result.Length <= 4000);
    }

    /// <summary>
    /// Lines are ranked, not taken greedily in order: a newer line that does not fit is skipped and
    /// an older, shorter one may still use what is left of the budget.
    /// </summary>
    [Fact]
    public void SkipsCandidateThatDoesNotFitAndKeepsFillingWithSmallerOnes()
    {
        var lines = Lines("short a", "a much longer line of text here", "newest");
        Assert.Equal("short a\nnewest", VisualContextExcerptSelector.Select(lines, "", focusBounds: null, maxCharacters: 15));
    }

    /// <summary>Duplicates are found case-insensitively after sanitizing, and the earliest copy stays.</summary>
    [Fact]
    public void DedupesCaseInsensitivelyKeepingEarliest()
    {
        var lines = Lines("Hello There friend", "hello there friend", "Other line here");
        Assert.Equal(
            "Hello There friend\nOther line here",
            VisualContextExcerptSelector.Select(lines, "", focusBounds: null, maxCharacters: 4000));
    }

    /// <summary>Prompt-shaped punctuation becomes spaces and collapses, but digits survive.</summary>
    [Fact]
    public void SanitizesPunctuationButKeepsNumbers()
    {
        Assert.Equal("Invoice 42 450", Select([new OcrLine("Invoice #42: $450!", 1f)], budget: 4000));
    }

    [Fact]
    public void NonPositiveBudgetReturnsEmpty()
    {
        Assert.Equal("", Select(Lines("Project agenda"), budget: -1));
    }
}
