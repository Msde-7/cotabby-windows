namespace Cotabby.Core.Support;

/// <summary>
/// Keeps every suggestion inside the user's word-count preset so consecutive ghosts read as the same
/// kind of thing. <see cref="DecodeStopPolicy"/> holds the lower bound during decoding. This policy
/// handles the top. Text past the maximum is cut at a word boundary, and a clause boundary inside the
/// window is preferred so the ghost ends where a reader would pause.
/// </summary>
public static class SuggestionLengthPolicy
{
    /// <summary>Punctuation after which a suggestion may end early inside the window.</summary>
    private const string ClauseBoundaries = ",;:.!?—–";

    /// <summary>
    /// Returns <paramref name="text"/> trimmed to at most <paramref name="maximum"/> words, leading
    /// whitespace preserved. When at least <paramref name="minimum"/> words fit before a clause
    /// boundary inside the window, the text ends there.
    /// </summary>
    public static string Trimmed(string text, int minimum, int maximum)
    {
        if (maximum <= 0) return text;
        var graphemes = new GraphemeString(text);
        var words = graphemes.Split(g => g.IsWhitespace);
        if (words.Count <= maximum) return text;

        // Prefer a clause boundary between the minimum and the maximum word, latest first.
        for (int index = maximum - 1; index >= Math.Max(minimum, 1) - 1; index--)
        {
            if (GraphemeString.LastOf(text[words[index]]) is { } last && last.IsAnyOf(ClauseBoundaries))
            {
                return text[..words[index].End];
            }
        }
        return text[..words[maximum - 1].End];
    }
}
