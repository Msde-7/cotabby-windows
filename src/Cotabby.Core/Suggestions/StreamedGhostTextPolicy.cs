using Cotabby.Core.Support;
using Cotabby.Core.Text;

namespace Cotabby.Core.Suggestions;

/// <summary>
/// Decides which streamed text can show now. A streamed render must strictly extend what is
/// already on screen: partials can arrive out of order, and the cleanup run on each one does not
/// promise that a longer raw text cleans up to a longer suggestion. Dropping a non-extension
/// costs nothing, since the next partial or the final answer replaces it.
/// </summary>
public static class StreamedGhostTextPolicy
{
    /// <summary>
    /// The partial without an unfinished last word. Accepting stops generation, so keeping "ag"
    /// from "world ag" would turn a fragment into the next word offered. Characters already
    /// shown are never taken back, and a closing quote completes the word it closes.
    /// </summary>
    public static string CompletedBufferedPrediction(string text, int visibleCharacterCount)
    {
        var graphemes = new GraphemeString(text);
        int visible = Math.Clamp(visibleCharacterCount, 0, graphemes.Count);
        if (visible >= graphemes.Count) return text;
        var last = graphemes[graphemes.Count - 1];
        if (!(last.IsLetter || last.IsNumber || last.Is('_') || CaretWordContext.IsConnector(last))) return text;

        int tokenLength = graphemes.SuffixLength(g => !g.IsWhitespace && !g.IsNewline);
        var first = graphemes[graphemes.Count - tokenLength];
        if ((last.Is('\'') && first.Is('\'')) || (last.Is('’') && first.Is('‘'))) return text;
        return graphemes.Prefix(Math.Max(visible, graphemes.Count - tokenLength));
    }

    /// <summary>True when <paramref name="candidate"/> is longer than what shows and begins with it.</summary>
    public static bool IsRenderableExtension(string candidate, string? currentlyRendered)
    {
        if (candidate.Length == 0) return false;
        if (string.IsNullOrEmpty(currentlyRendered)) return true;
        return GraphemeString.CountOf(candidate) > GraphemeString.CountOf(currentlyRendered)
            && candidate.StartsWith(currentlyRendered, StringComparison.Ordinal);
    }
}
