using Cotabby.Core.Focus;

namespace Cotabby.Core.Emoji;

/// <summary>
/// Geometry of the two row emoji picker, a typed query row above a horizontal ribbon of ranked
/// glyphs. The width hugs the ribbon's cells up to <see cref="MaxVisibleCells"/>, then the ribbon
/// scrolls. Values are device independent units.
/// </summary>
public static class EmojiPickerMetrics
{
    /// <summary>Square cell that holds one glyph and its selection chip.</summary>
    public const double CellSize = 30;

    /// <summary>Gap between adjacent ribbon cells.</summary>
    public const double CellSpacing = 2;

    /// <summary>Leading and trailing inset shared by the query row and the ribbon.</summary>
    public const double HorizontalInset = 8;

    public const double QueryRowHeight = 22;
    public const double RibbonRowHeight = 40;

    /// <summary>How many cells are shown before the ribbon scrolls horizontally.</summary>
    public const int MaxVisibleCells = 8;

    /// <summary>Floor so a one or two match ribbon or the empty state never collapses to a sliver.</summary>
    public const double MinWidth = 132;

    /// <summary>The panel size for a number of matches. An empty result still reserves the ribbon row.</summary>
    public static (double Width, double Height) ContentSize(int matchCount)
    {
        int cells = Math.Min(matchCount, MaxVisibleCells);
        double ribbonWidth = cells <= 0
            ? MinWidth
            : cells * CellSize + (cells - 1) * CellSpacing + HorizontalInset * 2;
        return (Math.Max(MinWidth, ribbonWidth), QueryRowHeight + RibbonRowHeight);
    }
}

/// <summary>
/// Places the picker panel relative to the caret. Rectangles use screen coordinates with a top
/// left origin, so "below the caret" means a larger y.
/// </summary>
public static class EmojiPickerPanelLayout
{
    /// <summary>Vertical gap between the caret and the panel edge.</summary>
    public const double CaretGap = 6;

    /// <summary>
    /// Positions the panel below the caret when it fits, flips it above when the caret is near the
    /// bottom of the screen, and clamps to the visible frame on every edge.
    /// </summary>
    public static ScreenRect Frame(ScreenRect caretRect, double width, double height, ScreenRect visibleFrame)
    {
        double originX = caretRect.X;
        if (originX + width > visibleFrame.Right) originX = visibleFrame.Right - width;
        originX = Math.Max(originX, visibleFrame.X);

        double belowOriginY = caretRect.Bottom + CaretGap;
        double aboveOriginY = caretRect.Y - CaretGap - height;

        double originY;
        if (belowOriginY + height <= visibleFrame.Bottom)
        {
            originY = belowOriginY;
        }
        else if (aboveOriginY >= visibleFrame.Y)
        {
            originY = aboveOriginY;
        }
        else
        {
            // Neither placement fits. Stay on screen, preferring the bottom edge.
            originY = Math.Min(visibleFrame.Bottom - height, Math.Max(belowOriginY, visibleFrame.Y));
        }

        return new ScreenRect(originX, originY, width, height);
    }
}
