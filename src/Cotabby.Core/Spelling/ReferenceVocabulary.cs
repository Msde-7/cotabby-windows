using Cotabby.Core.Support;
using Cotabby.Core.Text;

namespace Cotabby.Core.Spelling;

/// <summary>
/// The words already in the document and in the user's glossary. They keep spelling checks from
/// rejecting established jargon.
/// </summary>
public static class ReferenceVocabulary
{
    // Upstream measures these three windows in grapheme clusters. UTF-16 units are used here,
    // which only makes the windows slightly smaller for text outside the basic plane.
    private const int PrecedingWindow = 4000;
    private const int TrailingWindow = 2000;
    private const int GlossaryWindow = 1300;

    public static IReadOnlySet<string> Words(string precedingText, string trailingText, string glossary)
    {
        var preceding = precedingText[Math.Max(0, precedingText.Length - PrecedingWindow)..];
        if (CaretWordContext.UnfinishedWord(preceding) is { } partial)
        {
            preceding = preceding[..^partial.Length];
        }
        var source = string.Join(' ', preceding, Head(trailingText, TrailingWindow), Head(glossary, GlossaryWindow));

        var words = new HashSet<string>(StringComparer.Ordinal);
        foreach (var range in new GraphemeString(source).Split(c => !c.IsLetter && !CaretWordContext.IsConnector(c)))
        {
            var word = source[range];
            if (GraphemeString.FirstOf(word) is { IsLetter: true } && GraphemeString.LastOf(word) is { IsLetter: true })
            {
                words.Add(word);
            }
        }
        return words;
    }

    private static string Head(string text, int length) => text[..Math.Min(text.Length, length)];
}
