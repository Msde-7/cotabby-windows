using Cotabby.Core.Overlay.Font;

namespace Cotabby.Core.Tests.Overlay.Font;

/// <summary>
/// The first letter of a line was in the value while the caret still sat at the empty line's
/// start, x 342 on a line from 342, 677 wide.
/// </summary>
public sealed class CaretLagPolicyTests
{
    private readonly FakeGlyphMeasurer _m = new();

    private bool Lags(string text, double caretX, bool rightToLeft = false) =>
        CaretLagPolicy.CaretLagsTypedText(caretX, 342, 677, text, new GhostFace("Segoe UI"), 15, _m, rightToLeft);

    [Fact]
    public void ACaretStillAtTheLineStartBehindATypedLetterLags()
    {
        Assert.True(Lags("K", 342));
        Assert.True(Lags("Keep", 342.5));
    }

    [Fact]
    public void ACaretAfterTheTextDoesNotLag()
    {
        Assert.False(Lags("K", 351));
        Assert.False(Lags("Keep", 375));
    }

    [Fact]
    public void AnEmptyOrWhitespaceLineDecidesNothing()
    {
        Assert.False(Lags("", 342));
        Assert.False(Lags("  ", 342));
    }

    [Fact]
    public void TextThatMayHaveWrappedIsNotJudged() =>
        Assert.False(Lags(string.Concat(Enumerable.Repeat("a long line of words ", 8)), 342));

    [Fact]
    public void RightToLeftIsNotJudged() => Assert.False(Lags("K", 342, rightToLeft: true));
}
