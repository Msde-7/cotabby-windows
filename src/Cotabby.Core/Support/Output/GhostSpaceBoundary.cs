namespace Cotabby.Core.Support;

/// <summary>
/// The single rule for whether ghost text carries a leading space, evaluated against the text that
/// is in the field right now.
/// <para>
/// The normalizer decides the leading space from the preceding text as it was when generation
/// started. The user keeps typing during generation, so the decision can be stale both ways. A space
/// typed meanwhile leaves the ghost one space too far right, and a backspace that removed the space
/// makes accepting glue two words.
/// </para>
/// <para>
/// A base completion model writes the space itself. Its completion follows the request's preceding
/// text exactly, so one that starts without a space continues the word ("1" + "50K", "It'" + "s") and
/// one that starts with a space starts a new word. When the engine vouches for its spacing the
/// completion's own leading space decides, and the live text only removes a space the field already
/// has or restores one it lost. Engines that do not vouch for it get the word-character rule.
/// </para>
/// The rule is pure so it holds identically at generation, presentation and acceptance time.
/// <list type="number">
/// <item>A completion that continues the word before the caret never takes a space.</item>
/// <item>A completion opening with closing punctuation never takes a space.</item>
/// <item>Otherwise there is exactly one space before a new word, and never a space after whitespace
/// or an opening bracket.</item>
/// </list>
/// </summary>
public static class GhostSpaceBoundary
{
    /// <summary>What the model's own spacing says about the completion's first word.</summary>
    public enum ModelBoundary
    {
        /// <summary>The completion continues the word or number before the caret.</summary>
        SameWord,

        /// <summary>The completion starts a new word.</summary>
        NewWord,

        /// <summary>The engine does not vouch for its spacing.</summary>
        Unknown,
    }

    /// <summary>Punctuation that binds to the preceding word, so a space before it would be wrong.</summary>
    private const string BindingPunctuation = ",.;:!?…)]}'’\"%/-—";

    /// <summary>Characters after which a following space would be wrong even though they are not whitespace.</summary>
    private const string OpeningCharacters = "([{“‘/-@#$";

    /// <summary>Punctuation that can only attach to the word before it.</summary>
    private const string AttachingPunctuation = ",.;:!?…)]}%";

    /// <summary>
    /// The completion with exactly the right number of leading spaces for <paramref name="precedingText"/>.
    /// </summary>
    /// <param name="completion">The completion to re-space.</param>
    /// <param name="precedingText">The field's text before the caret now.</param>
    /// <param name="continuesPartialWord">True when the request was anchored at a word boundary, so
    /// the completion is what remains after the user's half-typed word.</param>
    /// <param name="requestPrecedingText">The text before the caret the request was built from, given
    /// only when the completion is exact text following it. Null for engines whose spacing cannot be
    /// trusted.</param>
    public static string Adjusted(
        string completion, string precedingText, bool continuesPartialWord,
        string? requestPrecedingText = null)
    {
        if (completion.Length == 0) return completion;
        string stripped = StripLeadingSpaces(completion);
        if (stripped.Length == 0) return completion;
        var boundary = BoundaryFor(
            leadsWithSpace: IsSpace(completion[0]), requestPrecedingText, continuesPartialWord);
        return NeedsLeadingSpace(stripped, precedingText, boundary) ? " " + stripped : stripped;
    }

    /// <summary>
    /// The completion re-spaced against the live text at presentation time. The full rule applies
    /// when the engine vouches for its spacing. For every other engine only the one always-safe
    /// correction is made, which drops a leading space the field now supplies itself.
    /// </summary>
    public static string LiveAdjusted(string completion, string precedingText, string? requestPrecedingText)
    {
        if (requestPrecedingText is not null)
        {
            return Adjusted(completion, precedingText, continuesPartialWord: false, requestPrecedingText);
        }
        return GraphemeString.LastOf(precedingText) is { IsWhitespace: true }
            ? StripLeadingSpaces(completion)
            : completion;
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

    /// <summary>Reads the model's own boundary decision from the completion's leading space.</summary>
    public static ModelBoundary BoundaryFor(
        bool leadsWithSpace, string? requestPrecedingText, bool continuesPartialWord)
    {
        if (continuesPartialWord)
        {
            // The remainder after the anchor starts with a space only when the model ended the
            // user's word there. Otherwise it is the rest of that word.
            return leadsWithSpace ? ModelBoundary.NewWord : ModelBoundary.SameWord;
        }
        if (requestPrecedingText is null) return ModelBoundary.Unknown;
        // After a request text that ended with whitespace the normalizer removed the model's
        // leading space, so the completion is a new word.
        if (GraphemeString.LastOf(requestPrecedingText) is not { IsWhitespace: false })
        {
            return ModelBoundary.NewWord;
        }
        return leadsWithSpace ? ModelBoundary.NewWord : ModelBoundary.SameWord;
    }

    /// <summary>Whether one space belongs between <paramref name="precedingText"/> and a completion that has none.</summary>
    public static bool NeedsLeadingSpace(string completion, string precedingText, ModelBoundary boundary)
    {
        if (boundary == ModelBoundary.SameWord) return false;
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
        if (last.Is('"') && precedingText.Count(c => c == '"') % 2 == 1) return false;
        if (boundary == ModelBoundary.NewWord) return true;
        // Unknown boundary. Word characters on both sides take a space, except the shapes that are
        // one word whatever the engine meant, which are a number continued by digits ("1" + "50K")
        // and a contraction after an in-word apostrophe ("It'" + "s").
        if (last.IsNumber && first.IsNumber) return false;
        if (IsInWordApostrophe(preceding) && first.IsLetter) return false;
        // Only a real word start earns a space.
        return (last.IsLetter || last.IsNumber || last.IsAnyOf(BindingPunctuation))
            && (first.IsLetter || first.IsNumber);
    }

    private static bool IsSpace(char character) => character is ' ' or '\u00A0';

    private static string StripLeadingSpaces(string text) => text.TrimStart(' ', '\u00A0');

    /// <summary>
    /// An apostrophe right after a letter ("It'", "don’") means the word goes on after it. Not when
    /// the word opened with a quote ("'hello'"), where the apostrophe closes the quotation.
    /// </summary>
    private static bool IsInWordApostrophe(GraphemeString text)
    {
        if (text.Count < 2 || !text[text.Count - 1].IsAnyOf("'’")) return false;
        if (!text[text.Count - 2].IsLetter) return false;
        var opening = text[text.Count - text.SuffixLength(g => !g.IsWhitespace)];
        return !opening.IsAnyOf("'‘\"");
    }
}
