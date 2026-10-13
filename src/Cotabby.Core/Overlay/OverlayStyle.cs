using Cotabby.Core.Focus;

namespace Cotabby.Core.Overlay;

/// <summary>How a suggestion is drawn.</summary>
public enum OverlayStyle
{
    /// <summary>Ghost text that continues the line at the caret.</summary>
    Inline,

    /// <summary>A card under the caret.</summary>
    Popup,

    /// <summary>A card under the caret that offers a spelling fix.</summary>
    Correction,
}

/// <summary>Where the user wants suggestions drawn.</summary>
public enum SuggestionDisplayMode
{
    Auto,
    Inline,
    Popup,
}

/// <summary>Picks the overlay style for a suggestion. Mirrors the macOS render mode policy.</summary>
public static class OverlayStylePolicy
{
    public static OverlayStyle StyleFor(SuggestionDisplayMode mode, FocusedField field, bool isCorrection)
    {
        if (isCorrection) return OverlayStyle.Correction;
        return mode switch
        {
            SuggestionDisplayMode.Inline => OverlayStyle.Inline,
            SuggestionDisplayMode.Popup => OverlayStyle.Popup,
            // Text after the caret belongs to the host, and inline ghost text there could only sit
            // on top of it. A caret whose position is only a guess cannot carry inline text either.
            _ => field.CaretIsEstimated || !IsCaretAtEndOfLine(field) ? OverlayStyle.Popup : OverlayStyle.Inline,
        };
    }

    private static bool IsCaretAtEndOfLine(FocusedField field)
    {
        foreach (char c in field.TrailingText)
        {
            if (c is '\n' or '\r') return true;
            if (!char.IsWhiteSpace(c)) return false;
        }
        return true;
    }
}
