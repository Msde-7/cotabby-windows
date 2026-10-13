using Cotabby.Core.Text;

namespace Cotabby.Core.Support;

/// <summary>
/// Decides whether a period actually ends a sentence, so sentence detection does not stop early on
/// decimals, list numbers, single-letter initials or common abbreviations. Only the period is
/// ambiguous. "!" and "?" are always terminal.
/// </summary>
public static class SentenceBoundaryClassifier
{
    /// <summary>Lowercased abbreviations whose trailing period is part of the word.</summary>
    private static readonly HashSet<string> Abbreviations = new(StringComparer.Ordinal)
    {
        "mr", "mrs", "ms", "dr", "st", "vs", "eg", "ie", "etc", "no", "fig", "approx", "inc", "ltd",
    };

    /// <summary>Ideographic full stop, fullwidth "!" and "?", and the halfwidth ideographic stop.</summary>
    internal const string CjkSentenceTerminators = "。！？｡";

    /// <summary>The CJK corner, fullwidth, lenticular and angle closers.</summary>
    internal const string CjkClosingPunctuation = "」』）】〉》｣";

    /// <summary>
    /// Closing punctuation that may follow a sentence terminator. Straight and curly quotes, round,
    /// square and curly brackets, plus the CJK closers.
    /// </summary>
    internal const string ClosingPunctuation = "\"')]}”’" + CjkClosingPunctuation;

    /// <summary>
    /// Whether <paramref name="text"/> ends at a sentence boundary. After skipping trailing
    /// whitespace and then a run of closing punctuation, the last visible character must be "!",
    /// "?", a CJK terminator or a terminal period. The skip lets <c>He said "stop."</c> and
    /// <c>(done!)</c> count as sentence ends.
    /// </summary>
    public static bool EndsSentence(string text)
    {
        var graphemes = new GraphemeString(text);
        int end = graphemes.Count;
        while (end > 0 && graphemes[end - 1].IsWhitespace) end--;
        while (end > 0 && graphemes[end - 1].IsAnyOf(ClosingPunctuation)) end--;
        if (end == 0) return false;

        var last = graphemes[end - 1];
        if (last.IsAnyOf("!?") || last.IsAnyOf(CjkSentenceTerminators)) return true;
        return last.Is('.') && IsTerminalPeriod(graphemes, end - 1);
    }

    /// <summary>
    /// Whether the period at UTF-16 offset <paramref name="periodIndex"/> ends a sentence. The caller
    /// guarantees that the character there is a period.
    /// </summary>
    public static bool IsTerminalPeriod(string text, int periodIndex)
    {
        var graphemes = new GraphemeString(text[..periodIndex]);
        return IsTerminalPeriod(graphemes, graphemes.Count);
    }

    private static bool IsTerminalPeriod(GraphemeString text, int periodIndex)
    {
        // A leading period has no preceding word to qualify it, so it is terminal.
        if (periodIndex == 0) return true;

        var before = text[periodIndex - 1];
        // Decimals, version numbers and list markers ("1.", "3.14") are not sentence ends.
        if (before.IsNumber) return false;
        if (!before.IsLetter) return true;

        // A single-letter initial ("U.", the "S." in "U.S.") stands alone.
        if (periodIndex < 2 || !text[periodIndex - 2].IsLetter) return false;

        int start = periodIndex;
        while (start > 0 && text[start - 1].IsLetter) start--;
        string letters = text.Text[text.Offset(start)..text.Offset(periodIndex)];
        return !Abbreviations.Contains(letters.ToLowerInvariant());
    }
}
