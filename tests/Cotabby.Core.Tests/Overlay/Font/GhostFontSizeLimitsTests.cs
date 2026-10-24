using Cotabby.Core.Overlay.Font;

namespace Cotabby.Core.Tests.Overlay.Font;

public sealed class GhostFontSizeLimitsTests
{
    [Theory]
    [InlineData(14, 11, 48, 14)]
    [InlineData(8, 11, 48, 11)]
    [InlineData(60, 11, 48, 48)]
    [InlineData(5, 0, 0, 9)]
    [InlineData(100, 0, 0, 100)]
    public void ClampedHoldsTheSizeInsideTheLimits(double size, double floor, double ceiling, double expected) =>
        Assert.Equal(expected, GhostFontSizeLimits.Clamped(size, floor, ceiling));

    [Fact]
    public void TheBackstopWinsOverALowerFloor() =>
        Assert.Equal(GhostFontSizeLimits.AbsoluteMinimum, GhostFontSizeLimits.Clamped(4, 6, 48));

    [Fact]
    public void AnInvertedRangeLetsTheFloorWin() =>
        Assert.Equal(30, GhostFontSizeLimits.Clamped(20, 30, 16));

    [Theory]
    [InlineData(11, 48, 11, 48)]
    [InlineData(2, 200, 9, 96)]
    [InlineData(24, 16, 24, 24)]
    [InlineData(double.NaN, double.NaN, 11, 48)]
    public void NormalizedKeepsBothInRangeWithTheFloorBelowTheCeiling(
        double floor, double ceiling, double expectedFloor, double expectedCeiling) =>
        Assert.Equal((expectedFloor, expectedCeiling), GhostFontSizeLimits.Normalized(floor, ceiling));
}
