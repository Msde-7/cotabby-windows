using System.Globalization;
using System.Text;

namespace Cotabby.Core.Spelling;

/// <summary>
/// The macOS source walks Swift <c>Character</c> values, which are extended grapheme clusters
/// classified by their first scalar. These helpers give the spelling code the same view of a
/// UTF-16 string, so a decomposed accent or an emoji counts as one character here too.
/// </summary>
internal static class Graphemes
{
    public static string[] Split(string text)
    {
        var elements = new List<string>(text.Length);
        for (int index = 0; index < text.Length;)
        {
            int length = StringInfo.GetNextTextElementLength(text.AsSpan(index));
            elements.Add(text.Substring(index, length));
            index += length;
        }
        return elements.ToArray();
    }

    public static int Count(string text) => new StringInfo(text).LengthInTextElements;

    /// <summary>
    /// The run of non-whitespace text that ends <paramref name="text"/>. Every whitespace
    /// character is a single UTF-16 unit, so this scan does not need grapheme segmentation.
    /// </summary>
    public static string TrailingToken(string text)
    {
        int start = text.Length;
        while (start > 0 && !char.IsWhiteSpace(text[start - 1])) start--;
        return text[start..];
    }

    public static bool IsLetter(string grapheme)
    {
        var scalar = FirstScalar(grapheme);
        return Rune.IsLetter(scalar) || Rune.GetUnicodeCategory(scalar) == UnicodeCategory.LetterNumber;
    }

    public static bool IsNumber(string grapheme) => Rune.GetNumericValue(FirstScalar(grapheme)) >= 0;

    public static bool IsUppercase(string grapheme) =>
        grapheme.ToUpperInvariant() == grapheme && grapheme.ToLowerInvariant() != grapheme;

    public static bool Is(string grapheme, string characters) =>
        grapheme.Length == 1 && characters.Contains(grapheme[0]);

    /// <summary>
    /// Invariant uppercasing maps one scalar to one scalar and leaves ß alone. Swift expands it
    /// to SS, and German is a bundled dictionary, so that one expansion is applied here.
    /// </summary>
    public static string Uppercased(string text) => text.ToUpperInvariant().Replace("ß", "SS");

    /// <summary>True when <paramref name="text"/> starts with <paramref name="prefix"/> and the prefix ends on a grapheme boundary.</summary>
    public static bool HasPrefix(string text, string prefix)
    {
        if (!text.StartsWith(prefix, StringComparison.Ordinal)) return false;
        int index = 0;
        while (index < prefix.Length) index += StringInfo.GetNextTextElementLength(text.AsSpan(index));
        return index == prefix.Length;
    }

    /// <summary>
    /// Swift compares strings by canonical equivalence and .NET compares UTF-16 units. Typed text
    /// is composed to NFC before it meets the dictionaries, which are stored in NFC.
    /// </summary>
    public static string Canonical(string text)
    {
        try
        {
            return text.Normalize();
        }
        catch (ArgumentException)
        {
            // An unpaired surrogate cannot be normalized. Such text never matches a dictionary word.
            return text;
        }
    }

    private static Rune FirstScalar(string grapheme)
    {
        Rune.DecodeFromUtf16(grapheme, out var scalar, out _);
        return scalar;
    }
}
