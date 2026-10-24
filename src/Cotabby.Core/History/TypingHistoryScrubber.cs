using System.Text;
using System.Text.RegularExpressions;

namespace Cotabby.Core.History;

/// <summary>
/// Removes secret-like strings from text before it enters typing history.
/// <para>
/// History is stored for months and fed back into prompts, so a pasted API key, private key or card
/// number must not survive into it, even though the archive is encrypted and only the local model
/// reads it. Prose passes through untouched. Only shapes that are almost never typed by hand are
/// replaced.
/// </para>
/// </summary>
public static class TypingHistoryScrubber
{
    public const string Redaction = "[redacted]";

    /// <summary>Records longer than this keep only the part around the caret.</summary>
    public const int MaximumRecordCharacters = 12_000;

    private static readonly Regex PrivateKeyBlock = new(
        @"-----BEGIN [A-Z ]*PRIVATE KEY-----[\s\S]*?(-----END [A-Z ]*PRIVATE KEY-----|$)", RegexOptions.CultureInvariant);

    /// <summary>
    /// Known credential prefixes followed by a run of key characters: OpenAI and Anthropic sk-,
    /// GitHub tokens, Slack tokens and AWS access key ids.
    /// </summary>
    private static readonly Regex PrefixedCredential = new(
        @"\b(sk-[A-Za-z0-9_\-]{16,}|gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}"
        + @"|xox[abprs]-[A-Za-z0-9\-]{10,}|AKIA[0-9A-Z]{16})", RegexOptions.CultureInvariant);

    /// <summary>
    /// Card-number-length digit runs, 13 to 19 digits optionally grouped by single spaces or dashes.
    /// Payment fields on websites are ordinary text fields. Phone numbers stay below 13 digits.
    /// </summary>
    private static readonly Regex LongDigitRun = new(@"\b(?:\d[ \-]?){12,18}\d\b", RegexOptions.CultureInvariant);

    /// <summary>Long unbroken tokens: JWTs, hex digests, base64 secrets.</summary>
    private static readonly Regex LongMixedToken = new(@"[A-Za-z0-9_\-+/=.]{24,}", RegexOptions.CultureInvariant);

    /// <summary>
    /// Scrubs the text before and after the caret as one text and caps the pair, so the boundary
    /// between what the user typed and what was already there survives.
    /// <para>
    /// Scrubbed separately, a caret inside a pasted key would split it into two halves that each
    /// look harmless. A secret across the caret is redacted whole and counts as typed. When the
    /// field is too long the typed side keeps its end and the rest keeps its start.
    /// </para>
    /// </summary>
    public static (string Text, int TypedLength) Scrub(string before, string after)
    {
        var (redacted, boundary) = Redact(before + after, before.Length);
        string typed = redacted[..boundary];
        string rest = redacted[boundary..];
        int afterBudget = Math.Min(rest.Length, MaximumRecordCharacters / 6);
        if (typed.Length + rest.Length > MaximumRecordCharacters)
        {
            rest = Head(rest, afterBudget);
            typed = Tail(typed, MaximumRecordCharacters - rest.Length);
        }
        typed = typed.TrimStart();
        rest = rest.TrimEnd();
        if (rest.Length == 0) typed = typed.TrimEnd();
        return (typed + rest, typed.Length);
    }

    public static string Scrub(string text)
    {
        string result = Redact(text, 0).Text;
        return result.Length > MaximumRecordCharacters ? Tail(result, MaximumRecordCharacters) : result;
    }

    /// <summary>Replaces every secret-like span and reports where the boundary lands in the result.</summary>
    private static (string Text, int Boundary) Redact(string text, int boundary)
    {
        var result = (Text: text, Boundary: boundary);
        foreach (var expression in new[] { PrivateKeyBlock, PrefixedCredential, LongDigitRun })
        {
            result = ReplaceMatches(expression, result.Text, result.Boundary, _ => Redaction);
        }
        // Long all-letter runs are real words in agglutinative languages and German compounds.
        // Only runs carrying digits look like generated secrets.
        return ReplaceMatches(LongMixedToken, result.Text, result.Boundary, token =>
            token.Any(char.IsDigit) && token.Any(char.IsLetter) ? Redaction : token);
    }

    /// <summary>
    /// Rewrites each match and carries the boundary across the edits. A boundary inside a replaced
    /// match moves to the end of the replacement, so the secret is never split. Inside a kept match
    /// it keeps its place.
    /// </summary>
    private static (string Text, int Boundary) ReplaceMatches(
        Regex expression, string text, int boundary, Func<string, string> transform)
    {
        var output = new StringBuilder(text.Length);
        int? mappedBoundary = null;
        int cursor = 0;
        foreach (Match match in expression.Matches(text))
        {
            string token = match.Value;
            string replacement = transform(token);
            int unchangedLength = match.Index - cursor;
            if (mappedBoundary is null && boundary < match.Index + match.Length)
            {
                mappedBoundary = boundary <= match.Index || replacement == token
                    ? output.Length + boundary - cursor
                    : output.Length + unchangedLength + replacement.Length;
            }
            output.Append(text, cursor, unchangedLength).Append(replacement);
            cursor = match.Index + match.Length;
        }
        int end = output.Length;
        output.Append(text, cursor, text.Length - cursor);
        return (output.ToString(), mappedBoundary ?? end + boundary - cursor);
    }

    /// <summary>The first <paramref name="length"/> units, without splitting a surrogate pair.</summary>
    private static string Head(string text, int length)
    {
        if (length >= text.Length) return text;
        if (length > 0 && char.IsHighSurrogate(text[length - 1])) length--;
        return text[..length];
    }

    /// <summary>The last <paramref name="length"/> units, without splitting a surrogate pair.</summary>
    private static string Tail(string text, int length)
    {
        if (length >= text.Length) return text;
        int start = text.Length - length;
        if (start < text.Length && char.IsLowSurrogate(text[start])) start++;
        return text[start..];
    }
}
