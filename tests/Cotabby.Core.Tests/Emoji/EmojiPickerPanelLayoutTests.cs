using Cotabby.Core.Emoji;
using Cotabby.Core.Focus;

namespace Cotabby.Core.Tests.Emoji;

/// <summary>
/// Screen coordinates have a top left origin here, so "below the caret" means a larger y. Upstream's
/// cases are mirrored vertically to match.
/// </summary>
public class EmojiPickerPanelLayoutTests
{
    private const double RowsHeight = EmojiPickerMetrics.QueryRowHeight + EmojiPickerMetrics.RibbonRowHeight;

    private static readonly ScreenRect VisibleFrame = new(0, 0, 1000, 800);

    private static ScreenRect Frame(ScreenRect caret, int matchCount, ScreenRect visibleFrame)
    {
        var (width, height) = EmojiPickerMetrics.ContentSize(matchCount);
        return EmojiPickerPanelLayout.Frame(caret, width, height, visibleFrame);
    }

    [Fact]
    public void ContentSize_ReservesRibbonRowWhenEmpty() =>
        Assert.Equal((EmojiPickerMetrics.MinWidth, RowsHeight), EmojiPickerMetrics.ContentSize(0));

    [Fact]
    public void ContentSize_CapsAtMaxVisibleCells()
    {
        double cells = EmojiPickerMetrics.MaxVisibleCells;
        double expectedWidth = cells * EmojiPickerMetrics.CellSize
            + (cells - 1) * EmojiPickerMetrics.CellSpacing
            + EmojiPickerMetrics.HorizontalInset * 2;

        Assert.Equal((expectedWidth, RowsHeight), EmojiPickerMetrics.ContentSize(20));
    }

    [Fact]
    public void ContentSize_HugsCellsBetweenTheFloorAndTheCap()
    {
        // One cell (30 + 2 * 8 insets = 46) is narrower than the floor, so the floor wins.
        Assert.Equal((EmojiPickerMetrics.MinWidth, RowsHeight), EmojiPickerMetrics.ContentSize(1));
        // Five cells are 5 * 30 + 4 * 2 spacing + 2 * 8 insets = 174, above the floor.
        Assert.Equal((174, RowsHeight), EmojiPickerMetrics.ContentSize(5));
        Assert.Equal(
            EmojiPickerMetrics.ContentSize(20),
            EmojiPickerMetrics.ContentSize(EmojiPickerMetrics.MaxVisibleCells));
    }

    [Fact]
    public void Frame_SitsBelowCaretWhenItFits()
    {
        var caret = new ScreenRect(200, 400, 2, 16);

        var frame = Frame(caret, 5, VisibleFrame);

        Assert.Equal(200, frame.X);
        Assert.Equal(caret.Bottom + EmojiPickerPanelLayout.CaretGap, frame.Y);
    }

    [Fact]
    public void Frame_FlipsAboveCaretNearBottom()
    {
        var caret = new ScreenRect(200, 764, 2, 16);

        var frame = Frame(caret, 5, VisibleFrame);

        Assert.Equal(caret.Y - EmojiPickerPanelLayout.CaretGap, frame.Bottom);
        Assert.True(frame.Y >= VisibleFrame.Y);
    }

    [Fact]
    public void Frame_TinyScreenPinsToTheBottomEdge()
    {
        // Neither below nor above fits a 50 tall visible frame, so the panel ends at the bottom
        // edge instead of hanging off it.
        var tinyFrame = new ScreenRect(0, 0, 400, 50);
        var caret = new ScreenRect(10, 14, 2, 16);

        var frame = Frame(caret, 3, tinyFrame);

        Assert.Equal(tinyFrame.Bottom, frame.Bottom);
        Assert.Equal(EmojiPickerMetrics.ContentSize(3), (frame.Width, frame.Height));
    }

    [Fact]
    public void Frame_RespectsANonZeroScreenOrigin()
    {
        // A secondary display to the left has negative x and clamping is relative to that frame.
        var secondary = new ScreenRect(-1200, 0, 1200, 800);
        var caret = new ScreenRect(-1250, 400, 2, 16);

        Assert.Equal(secondary.X, Frame(caret, 3, secondary).X);
    }

    [Fact]
    public void Frame_ClampsToRightEdge()
    {
        var caret = new ScreenRect(950, 400, 2, 16);

        Assert.Equal(VisibleFrame.Right, Frame(caret, 3, VisibleFrame).Right);
    }

    [Fact]
    public void Frame_ClampsToLeftEdge()
    {
        var caret = new ScreenRect(-50, 400, 2, 16);

        Assert.Equal(VisibleFrame.X, Frame(caret, 3, VisibleFrame).X);
    }
}
