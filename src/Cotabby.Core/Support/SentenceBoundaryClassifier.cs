namespace Cotabby.Core.Support;

/// <summary>
/// Decides whether generated text ends a sentence. Direct port of the macOS
/// <c>SentenceBoundaryClassifier</c>: used by the decode loop to stop at a
/// natural boundary and by phrase acceptance to find sentence ends.
/// </summary>
public static class SentenceBoundaryClassifier
{
    private static readonly HashSet<string> Abbreviations = new(StringComparer.Ordinal)
    {
        "mr", "mrs", "ms", "dr", "st", "vs", "eg", "ie", "etc", "no", "fig", "approx", "inc", "ltd",
    };

    private const string ClosingPunctuation = " ')]}”’\"";

    /// <summary>True if <paramref name="text"/> ends a sentence: after skipping
    /// trailing whitespace and closing punctuation, the last visible char is
    /// '!' or '?', or a '.' that qualifies as a terminal period.</summary>
    public static bool EndsSentence(string text)
    {
        int i = text.Length - 1;
        while (i >= 0 && (char.IsWhiteSpace(text[i]) || ClosingPunctuation.Contains(text[i]))) i--;
        if (i < 0) return false;

        char c = text[i];
        if (c is '!' or '?') return true;
        if (c == '.') return IsTerminalPeriod(text, i);
        return false;
    }

    /// <summary>
    /// Disambiguates a period at <paramref name="periodIndex"/>: false when it
    /// follows a digit ("3.14"), a single letter ("J."), or a known
    /// abbreviation ("Dr.", "etc."); true otherwise.
    /// </summary>
    public static bool IsTerminalPeriod(string text, int periodIndex)
    {
        if (periodIndex <= 0) return false;
        char prev = text[periodIndex - 1];
        if (char.IsDigit(prev)) return false;

        // Walk the run of letters immediately before the period.
        int start = periodIndex;
        while (start > 0 && char.IsLetter(text[start - 1])) start--;
        int runLen = periodIndex - start;
        if (runLen == 0) return true;
        if (runLen == 1) return false; // single-letter initial: "J. Smith"

        var run = text[start..periodIndex].ToLowerInvariant();
        return !Abbreviations.Contains(run);
    }
}
