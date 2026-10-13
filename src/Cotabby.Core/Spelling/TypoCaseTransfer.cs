using Cotabby.Core.Text;

namespace Cotabby.Core.Spelling;

/// <summary>
/// Copies a typo's capitalization onto its correction. Dictionaries hold lowercase words, so
/// without this a typo like <c>Teh</c> would be fixed to a lowercase <c>the</c>.
/// </summary>
public static class TypoCaseTransfer
{
    /// <summary>
    /// Returns <paramref name="correction"/> recased like <paramref name="source"/>. An all
    /// uppercase source of more than one letter uppercases the correction, a leading capital
    /// capitalizes its first letter, and anything else leaves it unchanged.
    /// </summary>
    public static string Apply(string source, string correction)
    {
        var sourceLetters = new GraphemeString(source).Where(c => c.IsLetter).ToArray();
        if (correction.Length == 0 || sourceLetters.Length == 0) return correction;

        if (sourceLetters.Length > 1 && sourceLetters.All(IsUppercase))
        {
            return Uppercased(correction);
        }
        if (IsUppercase(sourceLetters[0]))
        {
            int first = new GraphemeString(correction).Offset(1);
            return Uppercased(correction[..first]) + correction[first..];
        }
        return correction;
    }

    /// <summary>
    /// Cased and unchanged by uppercasing, judged on the whole grapheme. <see cref="Grapheme.IsUppercase"/>
    /// reads the category of the first scalar instead, and the two disagree on about 500 scalars.
    /// </summary>
    internal static bool IsUppercase(Grapheme character)
    {
        var text = character.ToString();
        return text.ToUpperInvariant() == text && text.ToLowerInvariant() != text;
    }

    /// <summary>
    /// Invariant uppercasing maps one scalar to one scalar and leaves ß alone. Swift expands it
    /// to SS, and German is a bundled dictionary, so that one expansion is applied here.
    /// </summary>
    internal static string Uppercased(string text) => text.ToUpperInvariant().Replace("ß", "SS");
}
