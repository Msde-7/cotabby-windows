using System.Globalization;
using System.Text;
using Cotabby.Core.Support;

namespace Cotabby.Core.Suggestions;

/// <summary>
/// User-facing rules for how much of a suggestion one accept takes and what text
/// is actually typed for it. Mirrors the acceptance half of the macOS
/// <c>SuggestionSessionReconciler</c>.
/// </summary>
public static class AcceptanceChunker
{
    /// <summary>
    /// Finds the end of the first word of a space-less script run. The default is a
    /// script-run heuristic. The app swaps in ICU word breaking at startup.
    /// Arguments are the text, the run start and the run limit.
    /// </summary>
    public static Func<string, int, int, int?> SpacelessWordEnd { get; set; } = ScriptRunWordEnd;

    /// <summary>
    /// The next word-granularity chunk: leading whitespace plus one word. With
    /// <paramref name="autoAcceptTrailingPunctuation"/> off, trailing punctuation is
    /// left for the next accept while punctuation inside a word is kept.
    /// </summary>
    public static string NextChunk(string remaining, bool autoAcceptTrailingPunctuation = true)
    {
        if (remaining.Length == 0) return string.Empty;

        int index = 0;
        while (index < remaining.Length && char.IsWhiteSpace(remaining[index])) index++;
        int tokenStart = index;
        while (index < remaining.Length && !char.IsWhiteSpace(remaining[index])) index++;

        if (tokenStart < index)
        {
            var first = Rune.GetRuneAt(remaining, tokenStart);
            if (BeginsSpacelessScriptWord(first)
                && SpacelessWordEnd(remaining, tokenStart, index) is { } wordEnd
                && wordEnd > tokenStart)
            {
                // Bind a following CJK punctuation run to the word so one accept takes both.
                index = EndOfCjkPunctuationRun(remaining, Math.Min(wordEnd, index), index, includingOpeners: false);
            }
            else if (BindsToPrecedingSpacelessWord(first) || IsCjkOpeningBracket(first))
            {
                index = EndOfCjkPunctuationRun(remaining, tokenStart, index, includingOpeners: true);
            }
        }

        if (!autoAcceptTrailingPunctuation
            && WordEndTrimmingTrailingPunctuation(remaining, tokenStart, index) is { } trimmed)
        {
            index = trimmed;
        }

        // A token with no word in it is punctuation the model attached to the previous
        // word. It binds to the word that follows so one accept takes both.
        if (autoAcceptTrailingPunctuation && tokenStart < index
            && WordBoundToLeadingPunctuation(remaining, tokenStart, index) is { } bound)
        {
            index = bound;
        }

        return remaining[..index];
    }

    /// <summary>
    /// The next phrase-granularity chunk: whole words up to a sentence terminator,
    /// a CJK clause comma, a newline, or the end of the suggestion.
    /// </summary>
    public static string NextPhrase(string remaining, bool autoAcceptTrailingPunctuation = true)
    {
        int consumed = 0;
        while (consumed < remaining.Length)
        {
            var chunk = NextChunk(remaining[consumed..], autoAcceptTrailingPunctuation);
            if (chunk.Length == 0) break;

            int newline = chunk.IndexOf('\n');
            if (newline >= 0) return remaining[..(consumed + newline + 1)];

            consumed += chunk.Length;
            if (EndsAtPhraseBoundary(remaining[..consumed])) break;
        }
        return remaining[..consumed];
    }

    /// <summary>
    /// The text to type for an accepted chunk. When the live text already ends in
    /// horizontal whitespace the chunk's own leading whitespace is dropped. A word
    /// boundary is never synthesized, so what is typed is what the ghost text showed.
    /// </summary>
    public static string InsertionChunk(string chunk, string precedingText)
    {
        if (precedingText.Length == 0 || !IsHorizontalWhitespace(precedingText[^1])) return chunk;
        int start = 0;
        while (start < chunk.Length && IsHorizontalWhitespace(chunk[start])) start++;
        return chunk[start..];
    }

    /// <summary>
    /// Appends one space when the chunk ends on a finished word. Used for the accept
    /// that exhausts a suggestion when "add space after accepting" is on.
    /// </summary>
    public static string AppendingTrailingSpace(string chunk) =>
        EndsOnFinishedWord(chunk) ? chunk + " " : chunk;

    /// <summary>
    /// Extends a word-ending chunk to also take the horizontal whitespace the
    /// suggestion already has after it, so the trailing space lands with this accept.
    /// </summary>
    public static string ConsumingTrailingSpace(string chunk, string remaining)
    {
        if (!EndsOnFinishedWord(chunk)) return chunk;
        int end = chunk.Length;
        while (end < remaining.Length && remaining[end] is ' ' or '\t') end++;
        return remaining[..end];
    }

    /// <summary>Counts word-like tokens so punctuation-only accepts do not inflate the word counter.</summary>
    public static int AcceptedWordCount(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Count(token => token.Any(char.IsLetterOrDigit));

    /// <summary>
    /// True when a fresh suggestion only repeats the chunk the user just accepted while
    /// the field still shows the text from before that accept. The host has not
    /// published the insert yet, so the model regenerated against stale text.
    /// </summary>
    public static bool IsStaleAcceptanceEcho(
        string resultText, string acceptedChunk, string currentPrecedingText, string acceptedPrecedingText)
    {
        var chunk = acceptedChunk.Trim();
        return chunk.Length > 0
            && resultText.Trim() == chunk
            && currentPrecedingText == acceptedPrecedingText;
    }

    private static bool EndsOnFinishedWord(string chunk)
    {
        if (chunk.Length == 0) return false;
        var last = LastRune(chunk, chunk.Length);
        return IsWordCharacter(last) && !BeginsSpacelessScriptWord(last);
    }

    private static int? WordBoundToLeadingPunctuation(string text, int start, int end)
    {
        var first = Rune.GetRuneAt(text, start);
        if (BeginsSpacelessScriptWord(first) || BindsToPrecedingSpacelessWord(first) || IsCjkOpeningBracket(first))
        {
            return null;
        }
        if (ContainsWordCharacter(text, start, end)) return null;

        int next = end;
        while (next < text.Length && char.IsWhiteSpace(text[next])) next++;
        int wordStart = next;
        while (next < text.Length && !char.IsWhiteSpace(text[next])) next++;
        if (wordStart == next
            || !ContainsWordCharacter(text, wordStart, next)
            || BeginsSpacelessScriptWord(Rune.GetRuneAt(text, wordStart)))
        {
            return null;
        }
        return next;
    }

    private static int EndOfCjkPunctuationRun(string text, int start, int limit, bool includingOpeners)
    {
        int cursor = start;
        while (cursor < limit)
        {
            var rune = Rune.GetRuneAt(text, cursor);
            if (!BindsToPrecedingSpacelessWord(rune) && !(includingOpeners && IsCjkOpeningBracket(rune))) break;
            cursor += rune.Utf16SequenceLength;
        }
        return cursor;
    }

    private static bool EndsAtPhraseBoundary(string text)
    {
        int index = text.Length;
        while (index > 0)
        {
            var closer = LastRune(text, index);
            if (!IsPhraseClosingPunctuation(closer)) break;
            index -= closer.Utf16SequenceLength;
        }
        if (index == 0) return false;

        var last = LastRune(text, index);
        if (IsPhraseClauseBoundary(last)) return true;
        if (last.Value == '.') return SentenceBoundaryClassifier.IsTerminalPeriod(text, index - 1);
        return last.Value is '!' or '?' || IsCjkSentenceTerminator(last);
    }

    /// <summary>
    /// The index just past the token's last word character when punctuation trails
    /// it. Null means the whole token is accepted.
    /// </summary>
    private static int? WordEndTrimmingTrailingPunctuation(string text, int tokenStart, int tokenEnd)
    {
        int? lastWordEnd = null;
        for (int i = tokenStart; i < tokenEnd;)
        {
            var rune = Rune.GetRuneAt(text, i);
            i += rune.Utf16SequenceLength;
            if (IsWordCharacter(rune)) lastWordEnd = i;
        }
        return lastWordEnd is { } end && end < tokenEnd ? end : null;
    }

    private static bool ContainsWordCharacter(string text, int start, int end)
    {
        for (int i = start; i < end;)
        {
            var rune = Rune.GetRuneAt(text, i);
            if (IsWordCharacter(rune)) return true;
            i += rune.Utf16SequenceLength;
        }
        return false;
    }

    public static int? ScriptRunWordEnd(string text, int start, int limit)
    {
        var first = Rune.GetRuneAt(text, start);
        int index = start + first.Utf16SequenceLength;
        var script = ScriptOf(first);
        // Kanji followed by its kana ending reads as one word.
        bool kanaTail = script == Script.Han;
        while (index < limit)
        {
            var rune = Rune.GetRuneAt(text, index);
            var next = ScriptOf(rune);
            if (next == Script.Other) break;
            if (next != script)
            {
                if (!(kanaTail && next == Script.Hiragana)) break;
                script = Script.Hiragana;
                kanaTail = false;
            }
            index += rune.Utf16SequenceLength;
        }
        return index;
    }

    private enum Script { Han, Hiragana, Katakana, Hangul, OtherSpaceless, Other }

    private static Script ScriptOf(Rune rune) => rune.Value switch
    {
        >= 0x3040 and <= 0x309F => Script.Hiragana,
        >= 0x30A0 and <= 0x30FF => Script.Katakana,
        >= 0xAC00 and <= 0xD7A3 or >= 0x1100 and <= 0x11FF => Script.Hangul,
        _ when BeginsSpacelessScriptWord(rune) => rune.Value >= 0x0E00 && rune.Value <= 0x17FF
            ? Script.OtherSpaceless
            : Script.Han,
        _ => Script.Other,
    };

    private static Rune LastRune(string text, int end)
    {
        int start = end - 1;
        if (start > 0 && char.IsLowSurrogate(text[start]) && char.IsHighSurrogate(text[start - 1])) start--;
        return Rune.GetRuneAt(text, start);
    }

    private static bool IsHorizontalWhitespace(char c) =>
        c == '\t' || char.GetUnicodeCategory(c) == UnicodeCategory.SpaceSeparator;

    private static bool IsWordCharacter(Rune rune) => Rune.IsLetter(rune) || Rune.IsNumber(rune);

    /// <summary>Scripts that write words without separating spaces.</summary>
    private static bool BeginsSpacelessScriptWord(Rune rune) => rune.Value switch
    {
        >= 0x3040 and <= 0x30FF => true,    // Hiragana and Katakana
        >= 0x3400 and <= 0x4DBF => true,    // CJK Extension A
        >= 0x4E00 and <= 0x9FFF => true,    // CJK Unified Ideographs
        >= 0xF900 and <= 0xFAFF => true,    // CJK Compatibility Ideographs
        >= 0xAC00 and <= 0xD7A3 => true,    // Hangul syllables
        >= 0x1100 and <= 0x11FF => true,    // Hangul Jamo
        >= 0x0E00 and <= 0x0E7F => true,    // Thai
        >= 0x0E80 and <= 0x0EFF => true,    // Lao
        >= 0x1780 and <= 0x17FF => true,    // Khmer
        >= 0x1000 and <= 0x109F => true,    // Myanmar
        >= 0x20000 and <= 0x2A6DF => true,  // CJK Extension B
        >= 0x30000 and <= 0x3134F => true,  // CJK Extension G
        _ => false,
    };

    private static bool IsCjkSentenceTerminator(Rune rune) =>
        rune.Value is 0x3002 or 0xFF01 or 0xFF1F or 0xFF61;

    private static bool IsCjkClosingPunctuation(Rune rune) =>
        rune.Value is 0x300D or 0x300F or 0xFF09 or 0x3011 or 0x3009 or 0x300B or 0xFF63;

    private static bool IsCjkOpeningBracket(Rune rune) =>
        rune.Value is 0x300C or 0x300E or 0xFF08 or 0x3010 or 0x3008 or 0x300A or 0xFF62;

    private static bool IsPhraseClauseBoundary(Rune rune) => rune.Value is 0x3001 or 0xFF0C or 0xFF64;

    private static bool IsPhraseClosingPunctuation(Rune rune) =>
        rune.Value is '"' or '\'' or ')' or ']' or '}' or 0x201D or 0x2019 || IsCjkClosingPunctuation(rune);

    private static bool BindsToPrecedingSpacelessWord(Rune rune) =>
        IsPhraseClauseBoundary(rune) || IsCjkSentenceTerminator(rune) || IsCjkClosingPunctuation(rune);
}
