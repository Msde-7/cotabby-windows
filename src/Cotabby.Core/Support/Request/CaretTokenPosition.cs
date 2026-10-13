using Cotabby.Core.Text;

namespace Cotabby.Core.Support;

/// <summary>
/// Where the caret sits relative to the token around it. Generation only makes sense at a token
/// end. A caret strictly inside a word, number, identifier or address ("head|phones", "3|:30",
/// "jane|@example.com") already has its continuation on the right, and every completion offered
/// there duplicates what follows or splices into the middle of something.
/// </summary>
public static class CaretTokenPosition
{
    /// <summary>
    /// Characters that glue a token together when a word character follows them, as in "3|:30",
    /// "jane|@example", "www|.example", "snake|_case", "kebab|-case" and "a|/b".
    /// </summary>
    private const string TokenJoiners = ":@./-_'’";

    public static bool IsInsideToken(string precedingText, string trailingText)
    {
        if (GraphemeString.LastOf(precedingText) is not { IsLetterOrNumber: true }) return false;
        var trailing = new GraphemeString(trailingText);
        if (trailing.First is not { } after) return false;
        if (after.IsLetterOrNumber) return true;
        return after.IsAnyOf(TokenJoiners) && trailing.Count > 1 && trailing[1].IsLetterOrNumber;
    }
}
