using Cotabby.Core.Context;

namespace Cotabby.Core.Focus;

/// <summary>
/// Gmail writes its Smart Compose suggestion into the compose body, followed by a "tab" key hint on
/// the next line, so UI Automation reads both as text after the caret. That span is Gmail's own
/// prediction: it is cut from the field, and Cotabby stays quiet while it shows.
/// </summary>
public static class SmartComposePolicy
{
    private const string Hint = "tab";

    /// <summary>Gmail suggests a few words, never a paragraph.</summary>
    private const int MaximumSuggestionLength = 160;

    /// <summary>The field without Gmail's suggestion and with <see cref="FocusedField.HostIsPredicting"/> set, or the field unchanged.</summary>
    public static FocusedField Apply(FocusedField field)
    {
        if (field.HasSelection || !IsGmail(field)) return field;
        int length = SuggestionLength(field.TrailingText);
        if (length == 0) return field;
        int caret = Math.Clamp(field.CaretOffset, 0, field.Text.Length);
        return field with { Text = field.Text.Remove(caret, length), HostIsPredicting = true };
    }

    /// <summary>
    /// The length of a Smart Compose suggestion at the start of <paramref name="trailing"/>, through
    /// its hint, or 0. The suggestion fills the rest of the caret's line and the hint sits alone on
    /// the next, so a signature further down stays the user's.
    /// </summary>
    public static int SuggestionLength(string trailing)
    {
        int lineBreak = trailing.IndexOf('\n');
        if (lineBreak <= 0 || lineBreak > MaximumSuggestionLength) return 0;
        int hintEnd = lineBreak + 1 + Hint.Length;
        if (hintEnd > trailing.Length || string.CompareOrdinal(trailing, lineBreak + 1, Hint, 0, Hint.Length) != 0) return 0;
        return hintEnd == trailing.Length || trailing[hintEnd] is '\r' or '\n' ? hintEnd : 0;
    }

    /// <summary>A browser tab titled for Gmail. Windows reads no page address, so the title stands in for it.</summary>
    private static bool IsGmail(FocusedField field) =>
        BrowserAppDetector.IsBrowser(field.ProcessName) && field.WindowTitle.Contains("Gmail", StringComparison.Ordinal);
}
