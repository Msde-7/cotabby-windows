namespace Cotabby.Core.Support;

/// <summary>
/// Decides which streamed text can be shown now or retained for later acceptance. Streamed renders
/// are monotonic. A candidate must strictly extend what is already on screen, because a shorter,
/// older cumulative partial can land after a longer one, and the normalizer's output for a longer
/// raw string is not guaranteed to extend its output for a shorter one. Dropping non-extensions
/// costs nothing, since the next partial or the final result supersedes them.
/// </summary>
public static class StreamedGhostTextPolicy
{
    /// <summary>
    /// A hidden trailing fragment has not been offered as a word yet. Retaining "ag" from
    /// "world ag" when acceptance stops generation would promote that fragment into the next
    /// acceptable word. Completed hidden words and their separators are kept, and the last token
    /// waits for a boundary. Already visible characters are never removed. Call this only for
    /// partials, because a final result needs no boundary to be complete.
    /// </summary>
    public static string CompletedBufferedPrediction(string text, int visibleCharacterCount)
    {
        var graphemes = new GraphemeString(text);
        int visibleCount = Math.Min(Math.Max(visibleCharacterCount, 0), graphemes.Count);
        if (visibleCount >= graphemes.Count || graphemes.Last is not { } last
            || !(last.IsLetter || last.IsNumber || last.Is('_') || CaretWordBoundary.IsConnector(last)))
        {
            return text;
        }

        int tokenCount = graphemes.SuffixLength(g => !g.IsWhitespace);
        var tokenFirst = graphemes[graphemes.Count - tokenCount];
        // A closing quote completes its quoted word. Apostrophes and hyphens elsewhere remain
        // lexical connectors, so "don't", "state-of-the-art" and a dangling "don'" still wait.
        if ((last.Is('\'') && tokenFirst.Is('\'')) || (last.Is('’') && tokenFirst.Is('‘'))) return text;
        return graphemes.Prefix(Math.Max(visibleCount, graphemes.Count - tokenCount));
    }

    public static bool IsRenderableExtension(string candidate, string? currentlyRendered)
    {
        if (candidate.Length == 0) return false;
        if (string.IsNullOrEmpty(currentlyRendered)) return true;
        var graphemes = new GraphemeString(candidate);
        return graphemes.Count > GraphemeString.CountOf(currentlyRendered) && graphemes.StartsWith(currentlyRendered);
    }
}
