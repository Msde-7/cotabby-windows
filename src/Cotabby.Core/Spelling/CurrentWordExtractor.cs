namespace Cotabby.Core.Spelling;

/// <summary>
/// Pulls the trailing word out of the text before the caret. A whitespace walk is fast,
/// deterministic and enough to answer whether the caret sits inside or just after a word.
/// </summary>
public static class CurrentWordExtractor
{
    private const string CodeLikeCharacters = "@/\\_:.#<>()[]{}$%^*=+|~`";

    /// <param name="Word">The trailing word.</param>
    /// <param name="CharacterCount">
    /// Grapheme clusters in the word. One Backspace removes one of them, so this is the number of
    /// presses that deletes the word. It can be smaller than the UTF-16 length.
    /// </param>
    public readonly record struct Result(string Word, int CharacterCount);

    /// <summary>
    /// Returns the trailing word at the caret, or null when the caret follows whitespace, the
    /// token does not look like natural language, or the token is a single character.
    /// </summary>
    public static Result? Extract(string precedingText)
    {
        var word = Graphemes.TrailingToken(precedingText);
        var characters = Graphemes.Split(word);
        return IsPlausibleNaturalWord(characters) ? new Result(word, characters.Length) : null;
    }

    /// <summary>
    /// Like <see cref="Extract"/>, but tolerates exactly one trailing space so a word the user just
    /// finished is still returned. Two trailing spaces or any other trailing whitespace return null.
    /// </summary>
    public static (Result Result, int TrailingSpaceCount)? ExtractTrailingWord(string precedingText)
    {
        int spaces = precedingText.EndsWith(' ') ? 1 : 0;
        return Extract(precedingText[..^spaces]) is { } result ? (result, spaces) : null;
    }

    /// <summary>
    /// Rejects tokens that are not natural-language words so variable names, URLs, mentions and
    /// numbers are never flagged. Missing a real typo is acceptable. Flagging code is not.
    /// </summary>
    private static bool IsPlausibleNaturalWord(string[] characters)
    {
        if (characters.Length < 2) return false;
        if (characters.Any(c => Graphemes.IsNumber(c) || Graphemes.Is(c, CodeLikeCharacters))) return false;

        // All-uppercase tokens are almost always acronyms, and correcting them is not useful.
        var letters = characters.Where(Graphemes.IsLetter).ToArray();
        return letters.Length == 0 || !letters.All(Graphemes.IsUppercase);
    }
}
