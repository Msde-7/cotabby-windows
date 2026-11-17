using Cotabby.Core.Text;
using System.Text;
using System.Text.RegularExpressions;

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
                AppendScalar(line, scalar);
            }
            else if (line.Length > 0 && line[^1] != ' ')
            {
                line.Append(' ');
            }
        }
        FlushLine(line, lines);
        return string.Join('\n', lines);
    }

    /// <summary><see cref="Sanitize(string)"/>, then cut to <paramref name="maxCharacters"/> characters.</summary>
    public static string Sanitize(string rawText, int maxCharacters) => Bounded(Sanitize(rawText), maxCharacters);

    /// <summary>
    /// Stricter cleaning for recognized screen text. OCR invents short mixed-case blobs, random
    /// ids, repeated glyphs and numeric chrome, which a model may copy as the next word. Each line
    /// keeps its real words and is dropped when most of its tokens are noise or none is a strong signal.
    /// </summary>
    public static string SanitizeOcr(string rawText, int? maxCharacters = null)
    {
        var lines = Sanitize(rawText).Split('\n').Select(FilterOcrNoiseLine).OfType<string>();
        string joined = string.Join('\n', lines);
        return maxCharacters is { } max ? Bounded(joined, max) : joined;
    }

    private static string Bounded(string text, int maxCharacters) =>
        ScalarSets.TrimWhitespaceAndNewlines(new GraphemeString(text).Prefix(Math.Max(0, maxCharacters)));

    private static readonly HashSet<string> PreservedShortWords = new(StringComparer.Ordinal)
    {
        "a", "i", "an", "am", "as", "at", "be", "by", "do", "go", "he",
        "if", "in", "is", "it", "me", "my", "no", "of", "on", "or", "so",
        "to", "up", "us", "we",
    };

    private static readonly HashSet<string> PreservedTechnicalTokens = new(StringComparer.Ordinal)
    {
        "ai", "api", "app", "apps", "ax", "bug", "bugs", "ci", "cmd", "css",
        "dom", "git", "gpu", "html", "http", "id", "ids", "io", "json", "llm",
        "ocr", "pdf", "pr", "prs", "qa", "sql", "ui", "url", "ux", "xpc",
    };

    private static readonly HashSet<string> CommonAcronyms = new(StringComparer.Ordinal)
    {
        "AI", "API", "AX", "CI", "CPU", "CSS", "DOM", "GPU", "HTML", "HTTP",
        "ID", "IO", "JSON", "LLM", "OCR", "PDF", "PR", "QA", "SQL", "UI",
        "URL", "UX", "XPC",
    };

    private static readonly string[] KnownWordSignals =
    [
        "accept", "app", "autocomplete", "button", "chat", "chrome", "class",
        "code", "cohamster", "context", "cotabby", "document", "email", "error", "field",
        "file", "fix", "function", "github", "google", "issue", "jira", "linear",
        "message", "model", "notion", "pane", "prompt", "pull", "request",
        "safari", "screen", "setting", "slack", "summary", "swift", "task",
        "test", "token", "user", "view", "xcode",
    ];

    private static string? FilterOcrNoiseLine(string line)
    {
        var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) return null;
        var assessed = tokens.Select(t => (Token: t, Verdict: AssessOcrToken(t))).ToList();
        var kept = assessed.Where(a => a.Verdict.Keep).Select(a => a.Token).ToList();
        // More than half noise is probably UI chrome.
        if (kept.Count * 2 < tokens.Length) return null;
        if (!assessed.Any(a => a.Verdict.Keep && a.Verdict.Strong)) return null;
        string result = string.Join(' ', kept);
        return result.Length == 0 ? null : result;
    }

    private static (bool Keep, bool Strong) AssessOcrToken(string token)
    {
        string lower = token.ToLowerInvariant();
        if (token.EnumerateRunes().All(Rune.IsNumber)) return (false, false);
        if (IsEmailLike(token) || IsFileOrDomainLike(token)) return (true, true);
        if (PreservedTechnicalTokens.Contains(lower) || CommonAcronyms.Contains(token)) return (true, true);
        if (IsRepeatedGlyphJunk(token)) return (false, false);
        // Other scripts and accented Latin carry real context but never match the English lists.
        if (token.EnumerateRunes().Any(r => r.Value > 127 && Rune.IsLetter(r))) return (true, true);
        return AssessLatinToken(token, lower);
    }

    private static (bool Keep, bool Strong) AssessLatinToken(string token, string lower)
    {
        if (GraphemeString.CountOf(token) <= 2) return (PreservedShortWords.Contains(lower), false);
        if (ContainsLetter(token) && token.EnumerateRunes().Any(Rune.IsNumber))
        {
            bool known = ContainsKnownWordSignal(lower);
            return (known, known);
        }
        if (IsLikelyShortMixedCaseNoise(token)) return (false, false);
        bool keep = HasWordSignal(lower);
        return (keep, keep);
    }

    private static bool IsEmailLike(string token)
    {
        var parts = token.Split('@');
        return parts.Length == 2 && ContainsLetter(parts[0]) && IsFileOrDomainLike(parts[1]);
    }

    private static bool IsFileOrDomainLike(string token)
    {
        var parts = token.Split('.');
        return parts.Length >= 2 && parts.All(p => p.Length > 0) && parts.Any(ContainsLetter);
    }

    private static bool ContainsLetter(string text) => text.EnumerateRunes().Any(Rune.IsLetter);

    private static bool ContainsKnownWordSignal(string lower) =>
        KnownWordSignals.Any(signal => lower.Contains(signal, StringComparison.Ordinal));

    private static bool HasWordSignal(string lower) =>
        ContainsLetter(lower) && (ContainsKnownWordSignal(lower) || lower.AsSpan().IndexOfAny("aeiouy") >= 0);

    private static bool IsRepeatedGlyphJunk(string token)
    {
        var scalars = token.ToLowerInvariant().EnumerateRunes().Where(ScalarSets.IsAlphanumeric).ToList();
        if (scalars.Count < 4) return false;
        int mostCommon = scalars.GroupBy(r => r).Max(g => g.Count());
        return mostCommon * 2 >= scalars.Count;
    }

    private static bool IsLikelyShortMixedCaseNoise(string token)
    {
        var letters = token.EnumerateRunes().Where(Rune.IsLetter).ToList();
        if (GraphemeString.CountOf(token) > 12 || letters.Count < 4) return false;
        int upper = letters.Count(Rune.IsUpper);
        int lower = letters.Count(Rune.IsLower);
        if (upper == 0 || lower == 0) return false;
        if (ContainsKnownWordSignal(token.ToLowerInvariant())) return false;
        // One leading capital is ordinary prose. Several capitals in a short token is usually noise.
        bool firstUpper = Rune.IsUpper(letters[0]);
        if (firstUpper && upper == 1) return false;
        return upper >= 2 || !firstUpper;
    }

    /// <summary>Lowercased runs of letters and numbers that are at least <paramref name="minimumLength"/> characters long.</summary>
    public static HashSet<string> SignificantTokens(string text, int minimumLength = 3)
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        var token = new StringBuilder();
        foreach (var scalar in text.ToLowerInvariant().EnumerateRunes())
        {
            if (ScalarSets.IsAlphanumeric(scalar)) AppendScalar(token, scalar);
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
        if (token.Length > 0 && token.ToString() is var text && GraphemeString.CountOf(text) >= minimumLength) tokens.Add(text);
        token.Clear();
    }

    private static void AppendScalar(StringBuilder builder, Rune scalar)
    {
        if (scalar.IsBmp)
        {
            builder.Append((char)scalar.Value);
            return;
        }
        int offset = scalar.Value - 0x10000;
        builder.Append((char)(0xD800 + (offset >> 10))).Append((char)(0xDC00 + (offset & 0x3FF)));
    }
}
