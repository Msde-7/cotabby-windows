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
        var sourceLetters = Graphemes.Split(source).Where(Graphemes.IsLetter).ToArray();
        if (correction.Length == 0 || sourceLetters.Length == 0) return correction;

        if (sourceLetters.Length > 1 && sourceLetters.All(Graphemes.IsUppercase))
        {
            return Graphemes.Uppercased(correction);
        }
        if (Graphemes.IsUppercase(sourceLetters[0]))
        {
            var first = Graphemes.Split(correction)[0];
            return Graphemes.Uppercased(first) + correction[first.Length..];
        }
        return correction;
    }
}
