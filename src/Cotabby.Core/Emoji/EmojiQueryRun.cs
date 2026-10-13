using System.Text.RegularExpressions;
using Cotabby.Core.Focus;

namespace Cotabby.Core.Emoji;

/// <summary>
/// Measures the trailing <c>:query</c> or <c>:query:</c> run before the caret, so a commit deletes
/// what is really in the field instead of trusting the typed character count. The run is ASCII,
/// so its UTF-16 length is also the number of backspaces that delete it.
/// </summary>
public static partial class EmojiQueryRun
{
    /// <summary>UTF-16 length of the run the text ends with, or null when it does not end with one.</summary>
    public static int? TrailingRunLength(string precedingText)
    {
        var match = TrailingRun().Match(precedingText);
        return match.Success ? match.Length : null;
    }

    /// <summary>
    /// How many UTF-16 units to delete before inserting the glyph. Uses the run measured before the
    /// caret of <paramref name="field"/> and falls back to the typed length when there is none.
    /// </summary>
    public static int DeleteCount(FocusedField field, int fallback)
    {
        int caret = Math.Clamp(field.CaretOffset, 0, field.Text.Length);
        return TrailingRunLength(field.Text[..caret]) ?? fallback;
    }

    [GeneratedRegex(@":[A-Za-z0-9_+\-]*:?\z")]
    private static partial Regex TrailingRun();
}
