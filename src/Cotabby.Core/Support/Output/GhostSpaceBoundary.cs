using Cotabby.Core.Text;

namespace Cotabby.Core.Support;

/// <summary>
/// The single rule for whether ghost text carries a leading space, evaluated against the text that
/// is in the field right now.
/// <para>
/// A base completion model writes the space itself. Its completion follows the request's preceding
/// text exactly, so one that starts without a space continues the word ("1" + "50K", "It'" + "s") and
/// one that starts with a space starts a new word. When the engine vouches for its spacing the
/// completion's own leading space decides, and the live text only removes a space the field already
/// has or restores one it lost. Other engines only lose a space the field now supplies.
/// </para>
/// <list type="number">
/// <item>A completion that continues the word before the caret never takes a space.</item>
/// <item>A completion opening with closing punctuation never takes a space.</item>
/// <item>Otherwise there is exactly one space before a new word, and never a space after whitespace
/// or an opening bracket.</item>
/// </list>
/// </summary>
public static class GhostSpaceBoundary
{
    /// <summary>Punctuation that binds to the preceding word, so a space before it would be wrong.</summary>
    private const string BindingPunctuation = ",.;:!?…)]}'’\"%/-—";

    /// <summary>Characters after which a following space would be wrong even though they are not whitespace.</summary>
    private const string OpeningCharacters = "([{“‘/-@#$";

    /// <summary>Punctuation that can only attach to the word before it.</summary>
    private const string AttachingPunctuation = ",.;:!?…)]}%";

    /// <summary>
    /// The completion with the leading space that fits <paramref name="precedingText"/>.
    /// </summary>
    /// <param name="completion">The completion to re-space.</param>
    /// <param name="precedingText">The field's text before the caret now.</param>
    /// <param name="requestPrecedingText">The text before the caret the request was built from, given
    /// only when the completion is exact text following it. Null for engines whose spacing cannot be
    /// trusted, which only lose a leading space the field now supplies itself.</param>
    public static string LiveAdjusted(string completion, string precedingText, string? requestPrecedingText)
    {
        string stripped = StripLeadingSpaces(completion);
        if (requestPrecedingText is null)
        {
            return GraphemeString.LastOf(precedingText) is { IsWhitespace: true } ? stripped : completion;
        }
        if (stripped.Length == 0) return completion;

        // After a request text that ended with whitespace the normalizer removed the model's
        // leading space, so the completion is a new word.
        bool startsNewWord = IsSpace(completion[0])
            || GraphemeString.LastOf(requestPrecedingText) is not { IsWhitespace: false };
        return startsNewWord && NeedsLeadingSpace(stripped, precedingText) ? " " + stripped : stripped;
    }

    /// <summary>
    /// True when the completion opens with punctuation that attaches to the word before the caret
    /// while the field now ends with whitespace. The user ended that word after the request was
    /// built, so the completion is stale ("Friday" then a typed space, then ". I'm going").
    /// </summary>
    public static bool IsStaleAfterTypedSpace(string completion, string precedingText)
    {
        if (GraphemeString.LastOf(precedingText) is not { IsWhitespace: true }) return false;
        return GraphemeString.FirstOf(StripLeadingSpaces(completion)) is { } first
            && first.IsAnyOf(AttachingPunctuation);
    }

    /// <summary>Whether one space belongs between <paramref name="precedingText"/> and a new word that has none.</summary>
    private static bool NeedsLeadingSpace(string completion, string precedingText)
    {
        var preceding = new GraphemeString(precedingText);
        if (GraphemeString.FirstOf(completion) is not { } first || preceding.Last is not { } last) return false;
        // After whitespace or a line break the boundary already exists, and a completion opening
        // with a line break needs none.
        if (last.IsWhitespace || first.IsWhitespace) return false;
        if (last.IsAnyOf(OpeningCharacters)) return false;
        if (first.IsAnyOf(BindingPunctuation)) return false;
        // A colon between digits is a ratio or a time ("1:1", "10:30"), not a clause boundary.
        if (last.Is(':') && first.IsNumber && preceding.Count >= 2 && preceding[preceding.Count - 2].IsNumber)
        {
            return false;
        }
        // A straight double quote closes a quotation only when it is the second of a pair. An
        // unpaired one is opening, and the quoted text follows it directly.
        return !(last.Is('"') && precedingText.Count(c => c == '"') % 2 == 1);
    }

    private static bool IsSpace(char character) => character is ' ' or '\u00A0';

    private static string StripLeadingSpaces(string text) => text.TrimStart(' ', '\u00A0');
}
