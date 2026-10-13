using System.Text;
using Cotabby.Core.Text;

namespace Cotabby.Core.Spelling;

/// <summary>
/// Pulls the trailing word out of the text before the caret. A whitespace walk is fast,
/// deterministic and enough to answer whether the caret sits inside or just after a word.
/// </summary>
public static class CurrentWordExtractor
{
    private const string CodeLikeCharacters = "@/\\_:.#<>()[]{}$%^*=+|~`";

    /// <summary>
    /// Returns the trailing word at the caret, or null when the caret follows whitespace, the
    /// token does not look like natural language, or the token is a single character.
    /// </summary>
    public static string? Extract(string precedingText)
    {
        var word = TrailingToken(precedingText);
        return IsPlausibleNaturalWord(new GraphemeString(word)) ? word : null;
    }

    /// <summary>
    /// The run of non-whitespace text that ends <paramref name="text"/>. Every whitespace
    /// character is a single UTF-16 unit, so this scan does not need grapheme segmentation.
    /// </summary>
    internal static string TrailingToken(string text)
    {
        int start = text.Length;
        while (start > 0 && !char.IsWhiteSpace(text[start - 1])) start--;
        return text[start..];
    }

    /// <summary>
    /// Rejects tokens that are not natural-language words so variable names, URLs, mentions and
    /// numbers are never flagged. Missing a real typo is acceptable. Flagging code is not.
    /// </summary>
    private static bool IsPlausibleNaturalWord(GraphemeString characters)
    {
        if (characters.Count < 2) return false;
        // Anything with a numeric value counts, which differs slightly from the Unicode number categories.
        if (characters.Any(c => Rune.GetNumericValue(c.Scalar) >= 0 || c.IsAnyOf(CodeLikeCharacters))) return false;

        // All-uppercase tokens are almost always acronyms, and correcting them is not useful.
        var letters = characters.Where(c => c.IsLetter).ToArray();
        return letters.Length == 0 || !letters.All(TypoCaseTransfer.IsUppercase);
    }
}
