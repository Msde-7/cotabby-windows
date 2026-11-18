using Cotabby.Core.Focus;

namespace Cotabby.Core.Tests.Focus;

public sealed class FieldReadsTests
{
    [Fact]
    public void UnionHoldsEveryRect() =>
        Assert.Equal(
            new ScreenRect(10, 5, 90, 35),
            FieldReads.Union([new ScreenRect(10, 20, 30, 20), new ScreenRect(50, 5, 50, 10)]));

    [Fact]
    public void UnionOfNothingIsEmpty()
    {
        Assert.Equal(ScreenRect.Empty, FieldReads.Union([]));
        // A caret-like rect with no width covers no line.
        Assert.Equal(ScreenRect.Empty, FieldReads.Union([new ScreenRect(40, 10, 0, 18)]));
    }

    [Fact]
    public void UnionCountsARectWithNoWidthAtItsPlace() =>
        Assert.Equal(
            new ScreenRect(5, 10, 45, 20),
            FieldReads.Union([new ScreenRect(5, 10, 0, 20), new ScreenRect(20, 10, 30, 20)]));

    [Fact]
    public void RectsOverlapOnlyWhenTheyShareArea()
    {
        var field = new ScreenRect(0, 0, 100, 50);
        Assert.True(FieldReads.Overlaps(new ScreenRect(90, 40, 20, 20), field));
        Assert.True(FieldReads.Overlaps(new ScreenRect(-10, -10, 200, 200), field));
        Assert.False(FieldReads.Overlaps(new ScreenRect(100, 0, 20, 20), field)); // touching edge
        Assert.False(FieldReads.Overlaps(new ScreenRect(0, 60, 100, 20), field));
    }

    [Fact]
    public void TextStartingOnTheCaretLineIsOnIt()
    {
        var caret = new ScreenRect(200, 100, 1, 20);
        Assert.True(FieldReads.IsOnCaretLine(new ScreenRect(10, 102, 190, 18), caret));
        Assert.True(FieldReads.IsOnCaretLine(new ScreenRect(10, 91, 190, 18), caret));
    }

    [Fact]
    public void TextReachingAnotherLineIsNotOnTheCaretLine()
    {
        var caret = new ScreenRect(200, 100, 1, 20);
        // Wrapped text starts a line above the caret.
        Assert.False(FieldReads.IsOnCaretLine(new ScreenRect(10, 80, 400, 40), caret));
        Assert.False(FieldReads.IsOnCaretLine(new ScreenRect(10, 90, 190, 18), caret));
        Assert.False(FieldReads.IsOnCaretLine(ScreenRect.Empty, caret));
    }

    [Fact]
    public void WithNoCaretRectAnyPaintedTextIsOnTheLine()
    {
        Assert.True(FieldReads.IsOnCaretLine(new ScreenRect(10, 80, 400, 40), ScreenRect.Empty));
        // A caret rect with no height still allows half a pixel.
        Assert.True(FieldReads.IsOnCaretLine(new ScreenRect(10, 100.4, 50, 18), new ScreenRect(60, 100, 1, 0)));
    }

    [Fact]
    public void AReadCutInsideAPairLosesTheHalfAtItsEnd()
    {
        // Lone surrogates are built in code: xUnit cannot carry them as theory data.
        string thumbsUp = char.ConvertFromUtf32(0x1F44D);
        Assert.Equal("abc", FieldReads.WithoutBrokenEnd("abc"));
        Assert.Equal("", FieldReads.WithoutBrokenEnd(""));
        Assert.Equal("ab", FieldReads.WithoutBrokenEnd("ab" + thumbsUp[0]));
        Assert.Equal("ab" + thumbsUp, FieldReads.WithoutBrokenEnd("ab" + thumbsUp));
    }

    [Fact]
    public void AKeptEndCutInsideAPairLosesTheHalfAtItsStart()
    {
        string thumbsUp = char.ConvertFromUtf32(0x1F44D);
        Assert.Equal("abc", FieldReads.WithoutBrokenStart("abc"));
        Assert.Equal("", FieldReads.WithoutBrokenStart(""));
        Assert.Equal("ab", FieldReads.WithoutBrokenStart(thumbsUp[1] + "ab"));
        Assert.Equal(thumbsUp + "ab", FieldReads.WithoutBrokenStart(thumbsUp + "ab"));
    }
}
