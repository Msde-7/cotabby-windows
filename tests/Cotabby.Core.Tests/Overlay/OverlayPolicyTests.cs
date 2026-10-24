using Cotabby.Core.Focus;
using Cotabby.Core.Overlay;

namespace Cotabby.Core.Tests.Overlay;

public sealed class SuggestionFadeInPolicyTests
{
    [Theory]
    [InlineData(true, false, false, true)]
    [InlineData(true, true, false, false)]
    [InlineData(true, false, true, false)]
    [InlineData(true, true, true, false)]
    [InlineData(false, false, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(false, true, true, false)]
    public void FadesOnlyOnAFirstPaintWithTheToggleOnAndMotionAllowed(
        bool enabled, bool wasVisible, bool reduceMotion, bool fades) =>
        Assert.Equal(fades, SuggestionFadeInPolicy.ShouldFadeIn(enabled, wasVisible, reduceMotion));
}

public sealed class TextDirectionDetectorTests
{
    [Theory]
    [InlineData("مرحبا بالعالم")]
    [InlineData("שלום עולם")]
    [InlineData("\u0710")]
    [InlineData("\uFB2A")]
    [InlineData("\uFE8D")]
    [InlineData("\u200F")]
    [InlineData("\u061C")]
    [InlineData("مرحبا   ")]
    [InlineData("مرحبا 123")]
    [InlineData("مرحبا!?")]
    [InlineData("hello مرحبا")]
    public void RightToLeftInputs(string text) => Assert.True(TextDirectionDetector.IsRightToLeft(text));

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("   ")]
    [InlineData("12345")]
    [InlineData("?!...")]
    [InlineData("hello world")]
    [InlineData("WORLD")]
    [InlineData("café")]
    [InlineData("αβγ")]
    [InlineData("привет")]
    [InlineData("中文")]
    [InlineData("\u0645\u0631\u062D\u0628\u0627\u200E")]
    [InlineData("مرحبا hello")]
    public void LeftToRightInputs(string? text) => Assert.False(TextDirectionDetector.IsRightToLeft(text));

    [Theory]
    [InlineData("\u058F", false)]
    [InlineData("\u0900", false)]
    [InlineData("\uFB1C", false)]
    [InlineData("\uFE6F", false)]
    [InlineData("\u0590", true)]
    [InlineData("\u08FF", true)]
    [InlineData("\uFB1D", true)]
    [InlineData("\uFDFF", true)]
    [InlineData("\uFE70", true)]
    [InlineData("\uFEFF", true)]
    public void RightToLeftBlockBoundariesAreExact(string text, bool rightToLeft) =>
        Assert.Equal(rightToLeft, TextDirectionDetector.IsRightToLeft(text));
}

public sealed class GhostWrapBandPolicyTests
{
    private static (double Left, double Right) Band(ScreenRect caret, ScreenRect field, double? lineLeft) =>
        GhostWrapBandPolicy.Band(new(caret, field, lineLeft, 0, 1512));

    [Fact]
    public void TheBandUsesTheFieldAndTheMeasuredPaddingOnBothSides()
    {
        var (left, right) = Band(new(567.4, 300, 0, 16), new(200, 300, 420, 194), 205);
        Assert.Equal(205, left, 3);
        Assert.Equal(615, right, 3);
    }

    [Fact]
    public void WithoutALineBoxTheDefaultInsetIsAssumedOnBothSides()
    {
        var (left, right) = Band(new(300, 300, 1, 18), new(100, 200, 500, 100), null);
        Assert.Equal(104, left, 3);
        Assert.Equal(596, right, 3);
    }

    [Fact]
    public void AnImplausibleLineLeftAnchorsTheLeftEdgeButNotTheRightInset()
    {
        var (left, right) = Band(new(500, 300, 1, 18), new(100, 200, 800, 100), 400);
        Assert.Equal(400, left, 3);
        Assert.Equal(896, right, 3);
    }

    [Fact]
    public void TheBandIsClampedToTheWorkArea()
    {
        var (left, right) = Band(new(1400, 300, 1, 18), new(1300, 200, 600, 100), null);
        Assert.Equal(1304, left, 3);
        Assert.Equal(1504, right, 3);
    }

    [Fact]
    public void NoFieldStartsRowsAtTheCaretAndEndsAtTheWorkArea()
    {
        var (left, right) = Band(new(640, 300, 1, 18), ScreenRect.Empty, null);
        Assert.Equal(640, left, 3);
        Assert.Equal(1504, right, 3);
    }

    [Fact]
    public void ALineBoxLeftOfTheFieldFallsBackToTheFieldsEdge()
    {
        var field = new ScreenRect(88, 327, 546, 257);
        var (left, right) = Band(new(500, 400, 1, 21), field, 44);
        Assert.Equal(92, left, 3);
        Assert.Equal(630, right, 3);
    }

    [Fact]
    public void ABelievableLineBoxStillPlacesTheBand()
    {
        var field = new ScreenRect(88, 327, 546, 257);
        var (left, right) = Band(new(500, 400, 1, 21), field, 101);
        Assert.Equal(101, left, 3);
        Assert.Equal(621, right, 3);
    }

    [Fact]
    public void InsetsScaleWithTheMonitor()
    {
        var (left, right) = GhostWrapBandPolicy.Band(new(new(300, 300, 1, 18), new(100, 200, 500, 100), null, 0, 1512, Scale: 2));
        Assert.Equal(108, left, 3);
        Assert.Equal(592, right, 3);
    }
}
