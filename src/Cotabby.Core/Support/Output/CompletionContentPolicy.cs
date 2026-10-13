using System.Text.RegularExpressions;

namespace Cotabby.Core.Support;

/// <summary>Why <see cref="CompletionContentPolicy"/> rejected a completion.</summary>
public enum CompletionContentRejection
{
    NoWordContent,
    PunctuationAfterSpace,
    Scaffolding,
    RepetitiveContent,
    CopiesPrecedingText,
}

/// <summary>
/// Content rules for a normalized completion that no font, geometry or model setting can fix.
/// Each shape was wrong essentially every time it appeared in the upstream eval.
/// <list type="bullet">
/// <item>No word content ("," or "…"). The model wanted to attach punctuation to the previous word.</item>
/// <item>Closing punctuation right after a typed space. Accepting ", regards" after "Best " gives "Best ,".</item>
/// <item>Scaffolding. Forum or chat residue, stray HTML in prose, or the model talking back about the prompt.</item>
/// <item>Repetition. A short word sequence looping back to back means the model is stuck.</item>
/// <item>Copying. The completion restarts the words just before the caret. Only that tail counts, because
/// a document that repeats a paragraph elsewhere makes reproducing it the right prediction.</item>
/// </list>
/// </summary>
public static partial class CompletionContentPolicy
{
    /// <summary>Words before the caret a completion must repeat to count as copying.</summary>
    public const int CopiedRunWords = 4;

    /// <summary>Characters before the caret the copied run is taken from.</summary>
    public const int CopySearchWindow = 120;

    /// <summary>Punctuation that closes or continues the previous word and never starts a new one.</summary>
    private const string ClosingPunctuation = ",.;:!?…";

    private static readonly string[] MetaPhrases =
    [
        "i'm not sure what you mean",
        "i am not sure what you mean",
        "as an ai",
        "as a language model",
        "i cannot help",
        "i can't help",
        "i don't understand the question",
    ];

    [GeneratedRegex(@"^\[/?[A-Za-z][A-Za-z0-9 _-]{0,24}\]")]
    private static partial Regex LeadingBracketMarker();

    [GeneratedRegex(@"</?[a-zA-Z][a-zA-Z0-9]*(\s[^<>]*)?>")]
    private static partial Regex HtmlTag();

    public static CompletionContentRejection? Rejection(string completion, string precedingText)
    {
        if (!HasWordContent(completion)) return CompletionContentRejection.NoWordContent;
        if (StartsWithClosingPunctuationAfterSpace(completion, precedingText))
        {
            return CompletionContentRejection.PunctuationAfterSpace;
        }
        if (LooksLikeScaffolding(completion, precedingText)) return CompletionContentRejection.Scaffolding;
        if (IsRepetitive(completion)) return CompletionContentRejection.RepetitiveContent;
        if (CopiesPrecedingText(completion, precedingText)) return CompletionContentRejection.CopiesPrecedingText;
        return null;
    }

    /// <summary>True when a sequence of one to three words repeats three or more times in a row.</summary>
    public static bool IsRepetitive(string completion)
    {
        var words = GraphemeString.Words(completion.ToLowerInvariant());
        for (int period = 1; period <= 3; period++)
        {
            if (words.Count < period * 3) continue;
            int run = 0;
            for (int index = period; index < words.Count; index++)
            {
                run = words[index] == words[index - period] ? run + 1 : 0;
                if (run >= period * 2) return true;
            }
        }
        return false;
    }

    /// <summary>
    /// True when the completion contains the last <see cref="CopiedRunWords"/> words before the caret
    /// in order. Punctuation and case are ignored so "s." matches "s". Four words are enough that
    /// ordinary shared phrasing ("thanks for the") never trips it.
    /// </summary>
    public static bool CopiesPrecedingText(string completion, string precedingText)
    {
        var words = ContentWords(completion);
        var recent = ContentWords(new GraphemeString(precedingText).Suffix(CopySearchWindow));
        const int run = CopiedRunWords;
        if (words.Count < run || recent.Count < run) return false;
        string tail = string.Join(' ', recent.GetRange(recent.Count - run, run));
        for (int start = 0; start <= words.Count - run; start++)
        {
            if (string.Join(' ', words.GetRange(start, run)) == tail) return true;
        }
        return false;
    }

    private static List<string> ContentWords(string text)
    {
        string lowered = text.ToLowerInvariant();
        var words = new List<string>();
        foreach (var range in new GraphemeString(lowered).Split(g => !g.IsLetter && !g.IsNumber && !g.Is('\'')))
        {
            words.Add(lowered[range]);
        }
        return words;
    }

    public static bool HasWordContent(string text)
    {
        var graphemes = new GraphemeString(text);
        return graphemes.PrefixLength(g => !g.IsLetter && !g.IsNumber) < graphemes.Count;
    }

    public static bool StartsWithClosingPunctuationAfterSpace(string completion, string precedingText)
    {
        if (GraphemeString.LastOf(precedingText) is not { } last || !last.IsAnyOf(" \t")) return false;
        return GraphemeString.FirstOf(completion) is { } first && first.IsAnyOf(ClosingPunctuation);
    }

    public static bool LooksLikeScaffolding(string completion, string precedingText)
    {
        string trimmed = ScalarSets.TrimWhitespaceAndNewlines(completion);
        string lowered = trimmed.ToLowerInvariant();
        // "[User 0001]", "[quote]", "[/b]" and similar bracketed markers opening the completion.
        if (LeadingBracketMarker().IsMatch(trimmed)) return true;
        // Social or forum UI chrome has several " · " separators in one line.
        if (trimmed.Split(" · ").Length >= 3) return true;
        // HTML tags in a field whose own text carries none.
        if (HtmlTag().IsMatch(trimmed) && !precedingText.Contains('<')) return true;
        foreach (string phrase in MetaPhrases)
        {
            if (lowered.StartsWith(phrase, StringComparison.Ordinal)
                || lowered.Contains(" " + phrase, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }
}
