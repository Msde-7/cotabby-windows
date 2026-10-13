namespace Cotabby.Core.Support;

/// <summary>
/// Measures how far a host field's caret travels when text is inserted. The accept-time overlay
/// slide and the predicted-caret fallback need the caret's true travel, not the ghost render font's
/// width of the same text. Ghost text is floored at a legible size while hosts commonly render
/// smaller, so a ghost-font measurement overshoots the real advance on every accepted word.
/// </summary>
public static class InsertedTextAdvance
{
    /// <summary>
    /// Width of a string in the host field's own resolved font. Supplied by the presentation layer,
    /// which owns font resolution.
    /// </summary>
    public delegate double HostFontMeasurer(string text);

    /// <summary>
    /// The caret's travel for <paramref name="text"/> using the best host measurement available.
    /// The observed average character width comes first, because it is a direct measurement of the
    /// host's rendered glyphs, and scales by UTF-16 length. The field's resolved font is second.
    /// Null when neither exists, so callers keep their previous approximation.
    /// </summary>
    public static double? Width(string text, double? observedCharWidth, HostFontMeasurer? measureInHostFont)
    {
        if (text.Length == 0) return null;
        if (observedCharWidth is > 0) return observedCharWidth.Value * text.Length;
        return Width(text, measureInHostFont);
    }

    /// <summary>
    /// Width of <paramref name="text"/> in the field's resolved font, or null when no usable font
    /// is known. Whitespace is measured as is, because a leading boundary space is real caret travel.
    /// </summary>
    public static double? Width(string text, HostFontMeasurer? measureInHostFont)
    {
        if (text.Length == 0 || measureInHostFont is null) return null;
        double width = measureInHostFont(text);
        return double.IsFinite(width) && width > 0 ? width : null;
    }
}
