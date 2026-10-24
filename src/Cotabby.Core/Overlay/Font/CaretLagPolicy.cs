namespace Cotabby.Core.Overlay.Font;

/// <summary>
/// Recognizes a caret the host has not yet moved for text it has already published. Ported from
/// the macOS <c>CaretLagPolicy</c>.
/// </summary>
/// <remarks>
/// A Chromium editor publishes a keystroke's text before its caret, so the first letter of a line
/// can be in the value while the caret still sits at the line's start. A caret at its line's
/// leading edge while the text before it on that line holds a glyph cannot be that text's caret,
/// as long as the text is too short to have wrapped. Whitespace alone decides nothing, since some
/// hosts collapse a leading space.
/// </remarks>
public static class CaretLagPolicy
{
    /// <summary>Distance past the line's leading edge within which a caret counts as at the edge.</summary>
    public const double EdgeTolerance = 1;

    /// <summary>Fraction of the line's width the text must stay under to be sure it did not wrap.</summary>
    public const double OneLineFraction = 0.8;

    /// <summary>
    /// True when <paramref name="caretX"/> is at the line's leading edge although the text before
    /// the caret on that line holds a glyph that fits well inside the line. Right-to-left lines are
    /// not judged.
    /// </summary>
    public static bool CaretLagsTypedText(
        double caretX, double lineLeft, double lineWidth, string textBeforeCaretOnLine,
        GhostFace face, double size, IGlyphMeasurer measurer, bool isRightToLeft)
    {
        if (isRightToLeft || !(lineWidth > 0) || Math.Abs(caretX - lineLeft) > EdgeTolerance) return false;
        if (!textBeforeCaretOnLine.Any(c => !char.IsWhiteSpace(c))) return false;
        double advance = measurer.Width(textBeforeCaretOnLine, face, size);
        return advance > 0 && advance < lineWidth * OneLineFraction;
    }
}
