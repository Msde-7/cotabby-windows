using System.Text.RegularExpressions;

namespace Cotabby.Core.Support;

/// <summary>Why a suggestion was suppressed to empty, for logging/diagnosis.</summary>
public enum SuppressionReason
{
    None,
    EmptyGeneration,
    NormalizedToEmpty,
    DuplicatesTrailingText,
    EchoesPrecedingText,
    UnsafeToInsert,
}

/// <summary>
/// One-shot cleanup of raw model output before display/insert. Direct port of
/// the macOS <c>SuggestionTextNormalizer.normalizeDetailed</c> — the exact
/// step order matters (echo stripping must run after line cutting, safety
/// gate last).
/// </summary>
public static partial class SuggestionTextNormalizer
{
    public readonly record struct NormalizeResult(string Text, SuppressionReason Reason)
    {
        public bool IsSuppressed => Text.Length == 0;
    }

    /// <summary>Leading scaffolding labels a model may echo from prompt-shaped
    /// training data. Anchored, case-insensitive, longest-first.</summary>
    private static readonly string[] ScaffoldingLabels =
    [
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
    ];

    [GeneratedRegex(@"<think>[\s\S]*?</think>", RegexOptions.IgnoreCase)]
    private static partial Regex ClosedThinkBlock();

    [GeneratedRegex(@"<think>[\s\S]*", RegexOptions.IgnoreCase)]
    private static partial Regex DanglingThinkBlock();

    public static NormalizeResult Normalize(
        string raw,
        string prompt,
        string precedingText,
        string trailingText,
        bool singleLine)
    {
        bool rawHadContent = raw.Trim().Length > 0;

        var text = raw.Replace("\r", string.Empty);
        text = text.Replace("<|im_end|>", string.Empty).Replace("<|im_start|>", string.Empty);
        text = ClosedThinkBlock().Replace(text, string.Empty);
        text = DanglingThinkBlock().Replace(text, string.Empty);

        // Strip a full prompt echo, then a prefix echo.
        if (prompt.Length > 0 && text.StartsWith(prompt, StringComparison.Ordinal))
        {
            text = text[prompt.Length..].Trim('\n', '\r');
        }
        if (precedingText.Length > 0 && text.StartsWith(precedingText, StringComparison.Ordinal))
        {
            text = text[precedingText.Length..];
        }

        text = TrimControlAndNewlineEdges(text);
        text = text.Trim('\n');
        text = StripLeadingScaffoldingLabels(text);
        text = text.Trim('\n');

        // Line handling: multi-line cuts at the first blank line; single-line
        // keeps only the first line.
        if (singleLine)
        {
            int nl = text.IndexOf('\n');
            if (nl >= 0) text = text[..nl];
        }
        else
        {
            int blank = text.IndexOf("\n\n", StringComparison.Ordinal);
            if (blank >= 0) text = text[..blank];
            text = text.Trim('\n');
        }

        if (TrailingDuplicationFilter.DuplicatesTrailingText(text, trailingText))
        {
            return new(string.Empty, SuppressionReason.DuplicatesTrailingText);
        }

        bool beforeEchoNonEmpty = text.Trim().Length > 0;
        text = StripEchoPrefix(text, precedingText);
        bool collapsedByEcho = beforeEchoNonEmpty && text.Trim().Length == 0;

        // If the user's text already ends in whitespace, drop leading
        // whitespace from the suggestion so we don't double a space.
        if (precedingText.Length > 0 && char.IsWhiteSpace(precedingText[^1]))
        {
            text = text.TrimStart(' ', '\t');
        }

        if (!InsertionSafetyGate.IsSafeToInsert(text))
        {
            var reason = collapsedByEcho
                ? SuppressionReason.EchoesPrecedingText
                : text.Any(c => !char.IsWhiteSpace(c))
                    ? SuppressionReason.UnsafeToInsert
                    : rawHadContent
                        ? SuppressionReason.NormalizedToEmpty
                        : SuppressionReason.EmptyGeneration;
            return new(string.Empty, reason);
        }

        return new(text, SuppressionReason.None);
    }

    /// <summary>
    /// Removes a word-level echo of the end of <paramref name="precedingText"/>
    /// from the start of <paramref name="suggestion"/>. Models frequently
    /// restate the last few typed words before continuing; inserting that
    /// echo would duplicate what's already on screen.
    /// </summary>
    internal static string StripEchoPrefix(string suggestion, string precedingText)
    {
        if (suggestion.Length == 0 || precedingText.Length == 0) return suggestion;

        var suggestionWords = SplitWords(suggestion);
        var precedingWords = SplitWords(precedingText);
        if (suggestionWords.Count == 0 || precedingWords.Count == 0) return suggestion;

        int depth = Math.Min(precedingWords.Count, 15);
        int bestOverlap = 0;
        for (int offset = 1; offset <= Math.Min(depth, suggestionWords.Count); offset++)
        {
            bool match = true;
            for (int i = 0; i < offset; i++)
            {
                if (!string.Equals(
                        precedingWords[^(offset - i)].Word,
                        suggestionWords[i].Word,
                        StringComparison.OrdinalIgnoreCase))
                {
                    match = false;
                    break;
                }
            }
            if (match) bestOverlap = offset;
        }

        if (bestOverlap == 0) return suggestion;
        if (bestOverlap >= suggestionWords.Count) return string.Empty;

        // Slice after the last echoed word, keeping the whitespace that
        // follows it (it separates the echo from the genuinely new text).
        var lastEchoed = suggestionWords[bestOverlap - 1];
        int cut = lastEchoed.Start + lastEchoed.Word.Length;
        return suggestion[cut..].TrimStart(' ', '\t');
    }

    private static List<(string Word, int Start)> SplitWords(string s)
    {
        var words = new List<(string, int)>();
        int i = 0;
        while (i < s.Length)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
            int start = i;
            while (i < s.Length && !char.IsWhiteSpace(s[i])) i++;
            if (i > start) words.Add((s[start..i], start));
        }
        return words;
    }

    private static string StripLeadingScaffoldingLabels(string text)
    {
        bool stripped = true;
        while (stripped)
        {
            stripped = false;
            var trimmed = text.TrimStart();
            foreach (var label in ScaffoldingLabels.OrderByDescending(l => l.Length))
            {
                if (trimmed.StartsWith(label, StringComparison.OrdinalIgnoreCase))
                {
                    text = trimmed[label.Length..];
                    stripped = true;
                    break;
                }
            }
        }
        return text;
    }

    private static string TrimControlAndNewlineEdges(string text)
    {
        int start = 0, end = text.Length;
        while (start < end && (char.IsControl(text[start]) || text[start] is '\n' or '\r')) start++;
        while (end > start && (char.IsControl(text[end - 1]) || text[end - 1] is '\n' or '\r')) end--;
        return text[start..end];
    }
}

/// <summary>
/// Final safety check before text may be displayed or inserted. Direct port
/// of the macOS <c>InsertionSafetyGate</c>.
/// </summary>
public static class InsertionSafetyGate
{
    public static bool IsSafeToInsert(string text)
    {
        if (text.Length == 0) return false;
        bool hasVisible = false;
        foreach (var c in text)
        {
            if (c == '�') return false;
            if (c != '\n' && c != '\t' && (char.IsControl(c) || c == '\x7F')) return false;
            if (!char.IsWhiteSpace(c)) hasVisible = true;
        }
        return hasVisible;
    }
}
