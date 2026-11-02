using Cotabby.Core.Focus;

namespace Cotabby.Core.Overlay;

/// <summary>Where the suggestion card sits. Ported from the macOS <c>MirrorOverlayLayout</c>.</summary>
public static class GhostCardPlacement
{
    /// <summary>
    /// The card's left edge. Its padding reaches back past the caret so the suggestion's first letter
    /// sits under the insertion point, or for right-to-left text its last letter does. A caret with
    /// no height marks no line, and the card is then centered under the field.
    /// </summary>
    /// <param name="leadingInset">Distance from the card's edge to its text: padding plus border.</param>
    public static double Left(ScreenRect caret, ScreenRect field, double cardWidth, double leadingInset, bool rightToLeft)
    {
        if (!(caret.Height > 0) && !field.IsEmpty) return field.X + (field.Width - cardWidth) / 2;
        return rightToLeft ? caret.X + leadingInset - cardWidth : caret.X - leadingInset;
    }

    /// <summary>The card shows the suggestion on one line, its runs of whitespace collapsed to single spaces.</summary>
    public static string CardText(string suggestion) =>
        string.Join(' ', suggestion.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>The card's top edge: just under the caret's line, or under the field when the caret marks none.</summary>
    public static double Top(ScreenRect caret, ScreenRect field, double gap) =>
        (!(caret.Height > 0) && !field.IsEmpty ? field.Bottom : caret.Bottom) + gap;
}
