namespace Cotabby.Core.Focus;

/// <summary>Judges from geometry whether a field holds one line of text.</summary>
public static class FieldShape
{
    /// <summary>A field taller than this many caret heights has room for several lines.</summary>
    private const double SingleLineHeightInCarets = 2.5;

    /// <summary>
    /// Accessibility reports many multi-line editors, Notepad among them, with the same control
    /// type as a one-line input. The caret height gives the line height, so a field several lines
    /// tall is treated as multi-line whatever its type says.
    /// </summary>
    public static bool IsSingleLine(bool isEditControl, ScreenRect field, ScreenRect caret)
    {
        if (!isEditControl) return false;
        if (field.IsEmpty || caret.IsEmpty) return true;
        return field.Height <= caret.Height * SingleLineHeightInCarets;
    }
}
