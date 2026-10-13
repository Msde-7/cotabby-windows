namespace Cotabby.Core.Support;

/// <summary>
/// The lexical word touching the caret, as the output and streaming policies need it. Upstream
/// these two members belong to <c>CaretWordContext</c>.
/// </summary>
internal static class CaretWordBoundary
{
    private const string ProseOpeners = "(\"'“‘«‹";

    /// <summary>
    /// The word still being typed at the end of <paramref name="text"/>, or null. Apostrophes and
    /// hyphens remain inside words. Code-like tokens and scripts without space-delimited words
    /// return null. A leading quote or parenthesis frames prose and does not finish the word.
    /// </summary>
    public static string? UnfinishedWord(string text)
    {
        var graphemes = new GraphemeString(text);
        int tokenStart = graphemes.Count - graphemes.SuffixLength(g => !g.IsWhitespace);
        int wordStart = tokenStart + graphemes.PrefixLength(g => g.IsAnyOf(ProseOpeners), tokenStart);
        if (wordStart == graphemes.Count || !graphemes[wordStart].IsLetter) return null;
        if (graphemes.PrefixLength(g => g.IsLetter || IsConnector(g), wordStart) != graphemes.Count - wordStart)
        {
            return null;
        }
        string word = text[graphemes.Offset(wordStart)..];
        foreach (var scalar in word.EnumerateRunes())
        {
            if (IsUnspacedScript(scalar.Value)) return null;
        }
        return word;
    }

    public static bool IsConnector(Grapheme character) => character.IsAnyOf("'’-");

    private static bool IsUnspacedScript(int scalar) =>
        scalar is (>= 0x0E00 and <= 0x0EFF) or (>= 0x3040 and <= 0x30FF) or (>= 0x3400 and <= 0x9FFF)
            or (>= 0xAC00 and <= 0xD7AF) or (>= 0x20000 and <= 0x3134F);
}
