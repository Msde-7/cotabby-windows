using System.Text;
using System.Text.RegularExpressions;
using Cotabby.Core.Support;

namespace Cotabby.Core.Context;

/// <summary>
/// Cleans prompt context that did not come from the focused field. Clipboard text can carry
/// terminal separators, Markdown fences, shell prompts and color escapes, which a small model
/// may copy back as output.
/// </summary>
public static partial class PromptContextSanitizer
{
    [GeneratedRegex(@"\u001B\[[0-?]*[ -/]*[@-~]")]
    private static partial Regex AnsiEscape();

    /// <summary>
    /// Keeps only letters, numbers, whitespace, <c>@</c> and <c>.</c>. Anything else becomes a
    /// space so word boundaries survive, then whitespace runs collapse and blank lines drop out.
    /// </summary>
    public static string Sanitize(string rawText)
    {
        var lines = new List<string>();
        var line = new StringBuilder();
        foreach (var scalar in AnsiEscape().Replace(rawText, " ").EnumerateRunes())
        {
            if (ScalarSets.IsNewline(scalar))
            {
                FlushLine(line, lines);
            }
            else if (ScalarSets.IsAlphanumeric(scalar) || scalar.Value is '@' or '.')
            {
                line.Append(scalar.ToString());
            }
            else if (line.Length > 0 && line[^1] != ' ')
            {
                line.Append(' ');
            }
        }
        FlushLine(line, lines);
        return string.Join('\n', lines);
    }

    /// <summary>Lowercased runs of letters and numbers that are at least <paramref name="minimumLength"/> characters long.</summary>
    public static HashSet<string> SignificantTokens(string text, int minimumLength = 3)
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        var token = new StringBuilder();
        foreach (var scalar in text.ToLowerInvariant().EnumerateRunes())
        {
            if (ScalarSets.IsAlphanumeric(scalar)) token.Append(scalar.ToString());
            else FlushToken(token, tokens, minimumLength);
        }
        FlushToken(token, tokens, minimumLength);
        return tokens;
    }

    public static bool ContainsAlphanumericSignal(string text)
    {
        foreach (var scalar in text.EnumerateRunes())
        {
            if (ScalarSets.IsAlphanumeric(scalar)) return true;
        }
        return false;
    }

    private static void FlushLine(StringBuilder line, List<string> lines)
    {
        string text = line.ToString().TrimEnd(' ');
        if (text.Length > 0) lines.Add(text);
        line.Clear();
    }

    private static void FlushToken(StringBuilder token, HashSet<string> tokens, int minimumLength)
    {
        if (token.Length > 0 && GraphemeString.CountOf(token.ToString()) >= minimumLength) tokens.Add(token.ToString());
        token.Clear();
    }
}
