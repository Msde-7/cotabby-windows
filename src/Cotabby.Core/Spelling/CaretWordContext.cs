using System.Text;

namespace Cotabby.Core.Spelling;

/// <summary>
/// A lexical snapshot of the caret, shared by correction and completion policy. A pause carries
/// no evidence that a word is finished. Only a delimiter commits a word.
/// </summary>
public static class CaretWordContext
{
    private const string ProseOpeners = "(\"'“‘«‹";
    private const string Connectors = "'’-";
    private const string CommittingPunctuation = ",;:!?";

    public readonly record struct CommittedWord(string Word, string Delimiter);

    /// <summary>
    /// The word being typed at the end of <paramref name="text"/>. Apostrophes and hyphens stay
    /// inside words. Code-like tokens and scripts without space-delimited words return null.
    /// </summary>
    public static string? UnfinishedWord(string text)
    {
        // An opening quote or parenthesis frames prose and is not part of the word. Only leading
        // openers are stripped, so punctuation inside call(wor, URLs and identifiers still excludes the token.
        var characters = Graphemes.Split(Graphemes.TrailingToken(text))
            .SkipWhile(c => Graphemes.Is(c, ProseOpeners))
            .ToArray();
        if (characters.Length == 0 || !Graphemes.IsLetter(characters[0])
            || !characters.All(c => Graphemes.IsLetter(c) || IsConnector(c)))
        {
            return null;
        }

        var word = string.Concat(characters);
        return word.EnumerateRunes().Any(IsUnspacedScript) ? null : word;
    }

    /// <summary>
    /// The word a delimiter just committed. The delimiter is one of <c>,;:!?</c>, a single space,
    /// or both in that order. A period is excluded because a dotted identifier or URL looks the
    /// same at this boundary, and any further whitespace means the writer has moved on.
    /// </summary>
    public static CommittedWord? CommittedWordIn(string text)
    {
        var body = text;
        var delimiter = "";
        if (body.EndsWith(' '))
        {
            body = body[..^1];
            delimiter = " ";
        }
        if (body.Length > 0 && CommittingPunctuation.Contains(body[^1]))
        {
            delimiter = body[^1] + delimiter;
            body = body[..^1];
        }

        if (delimiter.Length == 0 || CurrentWordExtractor.Extract(body) is not { } current
            || UnfinishedWord(body) != current.Word)
        {
            return null;
        }
        return new CommittedWord(current.Word, delimiter);
    }

    public static bool IsConnector(string character) => Graphemes.Is(character, Connectors);

    private static bool IsUnspacedScript(Rune scalar) => scalar.Value
        is (>= 0x0E00 and <= 0x0EFF) or (>= 0x3040 and <= 0x30FF) or (>= 0x3400 and <= 0x9FFF)
        or (>= 0xAC00 and <= 0xD7AF) or (>= 0x20000 and <= 0x3134F);
}
