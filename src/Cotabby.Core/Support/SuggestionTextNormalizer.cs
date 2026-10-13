using System.Text;
using System.Text.RegularExpressions;
using Cotabby.Core.Text;

namespace Cotabby.Core.Support;

/// <summary>
/// Why a raw completion was reduced to empty ghost text. Logging this separates "the model produced
/// nothing usable" from "a safety or echo filter dropped a real completion", which point at
/// opposite fixes.
/// </summary>
public enum CompletionSuppressionReason
{
    /// <summary>The model emitted only whitespace, or nothing.</summary>
    EmptyGeneration,

    /// <summary>Raw output existed but was entirely control markers, reasoning blocks, scaffolding
    /// labels or newlines.</summary>
    NormalizedToEmpty,

    /// <summary>The completion began by repeating text that already follows the caret.</summary>
    DuplicatesTrailingText,

    /// <summary>The completion echoed the tail of the preceding text in full.</summary>
    EchoesPrecedingText,

    /// <summary>Printable characters survived but carried control or replacement glyphs.</summary>
    UnsafeToInsert,

    /// <summary>Nothing but punctuation or symbols survived.</summary>
    NoWordContent,

    /// <summary>Closing punctuation offered right after the user typed a space.</summary>
    PunctuationAfterSpace,

    /// <summary>Forum or chat residue, stray markup, or the model talking back about the prompt.</summary>
    Scaffolding,

    /// <summary>A short word sequence looping back to back.</summary>
    RepetitiveContent,

    /// <summary>The completion restarted the words just before the caret.</summary>
    CopiesPrecedingText,
}

/// <summary>The request fields the normalizer reads.</summary>
/// <param name="Prompt">The rendered prompt, stripped when the model echoes it.</param>
/// <param name="PrefixText">The windowed caret prefix inside the prompt.</param>
/// <param name="PrecedingText">The field's text before the caret.</param>
/// <param name="TrailingText">The field's text after the caret.</param>
/// <param name="IsMultiLineEnabled">Whether a completion may span lines.</param>
/// <param name="WordRange">The word-count preset the suggestion is held to, if any.</param>
public sealed record SuggestionNormalizationRequest(
    string Prompt,
    string PrefixText,
    string PrecedingText,
    string TrailingText = "",
    bool IsMultiLineEnabled = false,
    SuggestionWordRange? WordRange = null);

/// <summary>
/// Outcome of normalizing one raw completion. <c>Suppression</c> is null whenever <c>Text</c> is
/// non-empty.
/// </summary>
public readonly record struct SuggestionNormalizationResult(string Text, CompletionSuppressionReason? Suppression);

/// <summary>
/// The last-mile cleanup that turns raw model output into inline ghost text. Pure, so the same
/// request and raw output always give the same suggestion.
/// </summary>
public static partial class SuggestionTextNormalizer
{
    /// <summary>
    /// Section-header labels the prompts use, plus close variants small models hallucinate. Matching
    /// is anchored to this set so user text that merely contains a colon ("Note: buy milk") is never
    /// treated as scaffolding. Longest first, so "Text before the caret:" wins over a shorter sibling.
    /// </summary>
    private static readonly string[] LabelsByLengthDescending = new[]
    {
        "Text before the caret:",
        "Text before caret:",
        "Text after the caret:",
        "Text after caret:",
        "User Profile Context:",
        "Your style preferences:",
        "Final instruction:",
        "Screen context:",
        "Screen content:",
        "User's clipboard:",
        "Continuation:",
        "Application:",
        "Task:",
        "App:",
    }.OrderByDescending(label => label.Length).ToArray();

    [GeneratedRegex(@"<think>[\s\S]*?</think>")]
    private static partial Regex CompleteThinkBlock();

    [GeneratedRegex(@"<think>[\s\S]*")]
    private static partial Regex DanglingThinkBlock();

    /// <summary>
    /// Normalizes one raw completion and, when the result is empty, attributes the suppression to a
    /// specific cause.
    /// </summary>
    public static SuggestionNormalizationResult NormalizeDetailed(
        string rawSuggestion, SuggestionNormalizationRequest request)
    {
        bool rawHadContent = !ScalarSets.IsBlank(rawSuggestion);
        string normalized = rawSuggestion.Replace("\r", string.Empty);

        // Opening and role markers are removed in place, and anything from a stop marker onward
        // (a hallucinated new turn) is truncated.
        normalized = ControlTokenMarkers.Sanitize(normalized);
        normalized = StripThinkBlocks(normalized);
        normalized = StripPromptEcho(normalized, request);
        normalized = ScalarSets.Trim(normalized, IsControlOrNewline);

        // Small models often emit leading newlines before the continuation. They are trimmed before
        // collapsing to one line, so "\ndelicious" is not misread as an empty first line.
        normalized = ScalarSets.Trim(normalized, ScalarSets.IsNewline);

        // Stripping a leading run of known labels also runs before the single-line collapse, so a
        // model that stacks label lines still surfaces the real continuation.
        normalized = StripLeadingScaffoldingLabels(normalized);
        normalized = ScalarSets.Trim(normalized, ScalarSets.IsNewline);

        if (request.IsMultiLineEnabled)
        {
            // Keep content up to the first blank line, which stops runaway paragraph generation.
            int blankLine = normalized.IndexOf("\n\n", StringComparison.Ordinal);
            if (blankLine >= 0) normalized = normalized[..blankLine];
            // A leading space can be the only boundary between the existing word and the next one,
            // so only trailing formatting is trimmed here.
            normalized = ScalarSets.TrimEnd(normalized, ScalarSets.IsWhitespaceOrNewline);
        }
        else
        {
            // Only the immediate continuation line is surfaced.
            int start = 0;
            while (start < normalized.Length && normalized[start] == '\n') start++;
            int end = normalized.IndexOf('\n', start);
            if (start < normalized.Length) normalized = end < 0 ? normalized[start..] : normalized[start..end];
        }

        // A completion that starts by repeating text already after the caret is unusable. Showing
        // only the remainder often produces confusing mid-word ghosts.
        if (TrailingDuplicationFilter.DuplicatesTrailingText(normalized, request.TrailingText))
        {
            return new(string.Empty, CompletionSuppressionReason.DuplicatesTrailingText);
        }

        // Strip leading words that repeat the tail of the preceding text. A full collapse here
        // means the model only re-emitted the preceding text.
        string beforeEchoStrip = normalized;
        normalized = StripEchoPrefix(normalized, request.PrecedingText);
        bool collapsedByEcho = beforeEchoStrip.Length > 0 && normalized.Length == 0;

        // Space management runs after echo suppression because stripping echoed words can expose a
        // leading space. When the preceding text already ends with whitespace that space is dropped.
        // Otherwise it passes through as the word boundary the user needs.
        if (ScalarSets.EndsWithWhitespace(request.PrecedingText))
        {
            var graphemes = new GraphemeString(normalized);
            normalized = normalized[graphemes.Offset(graphemes.PrefixLength(g => g.IsWhitespace))..];
        }

        if (!InsertionSafetyGate.IsSafeToInsert(normalized))
        {
            return new(string.Empty, SuppressionForEmptyResult(collapsedByEcho, rawHadContent, normalized));
        }

        if (CompletionContentPolicy.Rejection(normalized, request.PrecedingText) is { } rejection)
        {
            return new(string.Empty, Suppression(rejection));
        }

        if (request.WordRange is { } range && !request.IsMultiLineEnabled)
        {
            normalized = SuggestionLengthPolicy.Trimmed(normalized, range.LowWords, range.HighWords);
        }

        return new(normalized, null);
    }

    private static CompletionSuppressionReason Suppression(CompletionContentRejection rejection) => rejection switch
    {
        CompletionContentRejection.NoWordContent => CompletionSuppressionReason.NoWordContent,
        CompletionContentRejection.PunctuationAfterSpace => CompletionSuppressionReason.PunctuationAfterSpace,
        CompletionContentRejection.Scaffolding => CompletionSuppressionReason.Scaffolding,
        CompletionContentRejection.RepetitiveContent => CompletionSuppressionReason.RepetitiveContent,
        _ => CompletionSuppressionReason.CopiesPrecedingText,
    };

    /// <summary>Removes an echoed prompt, then an echoed caret prefix, from the front of the output.</summary>
    private static string StripPromptEcho(string text, SuggestionNormalizationRequest request)
    {
        string normalized = text;
        if (request.Prompt.Length > 0 && normalized.StartsWith(request.Prompt, StringComparison.Ordinal))
        {
            normalized = ScalarSets.Trim(normalized[request.Prompt.Length..], IsControlOrNewline);
        }
        if (request.PrefixText.Length > 0 && normalized.StartsWith(request.PrefixText, StringComparison.Ordinal))
        {
            normalized = normalized[request.PrefixText.Length..];
        }
        return normalized;
    }

    /// <summary>
    /// Names the most specific cause of an empty outcome at the safety gate, which rejects empty,
    /// whitespace-only and control-glyph output alike.
    /// </summary>
    private static CompletionSuppressionReason SuppressionForEmptyResult(
        bool collapsedByEcho, bool rawHadContent, string normalized)
    {
        if (collapsedByEcho) return CompletionSuppressionReason.EchoesPrecedingText;
        if (!ScalarSets.IsBlank(normalized)) return CompletionSuppressionReason.UnsafeToInsert;
        return rawHadContent
            ? CompletionSuppressionReason.NormalizedToEmpty
            : CompletionSuppressionReason.EmptyGeneration;
    }

    /// <summary>
    /// Removes the first complete reasoning block, then any dangling open tag left when generation
    /// hit the token limit before the block was closed.
    /// </summary>
    private static string StripThinkBlocks(string text)
    {
        if (!text.Contains("<think>", StringComparison.Ordinal)) return text;
        string result = CompleteThinkBlock().Replace(text, string.Empty, 1);
        return DanglingThinkBlock().Replace(result, string.Empty, 1);
    }

    /// <summary>
    /// Finds the longest run of words ending the preceding text that also starts the suggestion,
    /// then strips that overlap. Returns empty if the entire suggestion is echoed. Every offset in
    /// the preceding tail is tried, so "hi i like" + "i like to eat" finds the two-word overlap.
    /// </summary>
    private static string StripEchoPrefix(string suggestion, string precedingText)
    {
        var suggestionWords = new GraphemeString(suggestion).Split(g => g.IsWhitespace);
        if (suggestionWords.Count == 0) return suggestion;
        var precedingWords = GraphemeString.Words(precedingText);
        if (precedingWords.Count == 0) return suggestion;

        // If the model echoes 15 or more words the empty-result guard should drop the suggestion.
        int maxSearchDepth = Math.Min(precedingWords.Count, 15);
        int bestOverlap = 0;
        for (int offset = 1; offset <= Math.Min(maxSearchDepth, suggestionWords.Count); offset++)
        {
            bool matches = true;
            for (int index = 0; index < offset && matches; index++)
            {
                matches = string.Equals(
                    precedingWords[precedingWords.Count - offset + index],
                    suggestion[suggestionWords[index]],
                    StringComparison.OrdinalIgnoreCase);
            }
            if (matches) bestOverlap = offset;
        }

        if (bestOverlap == 0) return suggestion;
        if (bestOverlap >= suggestionWords.Count) return string.Empty;

        // Slice where the last echoed word ends, keeping the whitespace that follows it.
        return suggestion[suggestionWords[bestOverlap - 1].End..];
    }

    /// <summary>
    /// Removes a leading run of known scaffolding labels, whether each sits on its own line or inline
    /// before the continuation. A label appearing later in the text is left alone.
    /// </summary>
    private static string StripLeadingScaffoldingLabels(string text)
    {
        string working = text;
        while (true)
        {
            // Leading whitespace is dropped only when a label actually matches.
            var graphemes = new GraphemeString(working);
            string leading = working[graphemes.Offset(graphemes.PrefixLength(g => g.IsWhitespace))..];
            string? label = LabelsByLengthDescending.FirstOrDefault(
                candidate => leading.StartsWith(candidate, StringComparison.OrdinalIgnoreCase));
            if (label is null) return working;
            working = leading[label.Length..];
        }
    }

    private static bool IsControlOrNewline(Rune scalar) =>
        ScalarSets.IsControl(scalar) || ScalarSets.IsNewline(scalar);
}

/// <summary>
/// The single predicate for "is this safe to put on screen and insert". It rejects completions that
/// are non-empty but would insert nothing a user wants. Punctuation-only output is not judged here,
/// because a lone ")" or "." is a legitimate inline completion.
/// </summary>
public static class InsertionSafetyGate
{
    public static bool IsSafeToInsert(string completion)
    {
        if (completion.Length == 0) return false;

        bool sawNonWhitespace = false;
        foreach (var scalar in completion.EnumerateRunes())
        {
            // A replacement character means the detokenizer produced bytes it could not decode.
            if (scalar.Value == 0xFFFD) return false;
            // A line feed is content in a multi-line completion. Any other C0 control or DEL is
            // corruption.
            if (scalar.Value != 0x0A && (scalar.Value < 0x20 || scalar.Value == 0x7F)) return false;
            if (!ScalarSets.IsWhitespaceOrNewline(scalar)) sawNonWhitespace = true;
        }
        return sawNonWhitespace;
    }
}
