using Cotabby.Core.Text;

namespace Cotabby.Core.Support;

/// <summary>
/// Post-generation guard for visible output failures. Showing nothing beats presenting any of them
/// as an insertion.
/// <list type="bullet">
/// <item>Junk run. Four or more identical punctuation or symbol characters, unless the run extends
/// an identical run the user already has at the caret (continuing a "----" divider is legitimate).</item>
/// <item>Seam misspelling. With the caret inside a word, a joined word that is a correctable typo is
/// rejected ("gre" + "atful"). Unknown vocabulary stays eligible.</item>
/// <item>Leading-word misspelling. When the completion starts a new word, its first word is rejected
/// only when the caller can both identify it as a typo and offer a correction, so names and jargon
/// still pass.</item>
/// </list>
/// Code-like tokens and scripts without space-delimited words keep their normal path.
/// </summary>
public static class CompletionSeamGuard
{
    /// <summary>
    /// One spelling result per word. The guard only needs to tell actionable typos from unknown but
    /// uncorrectable vocabulary.
    /// </summary>
    public enum SpellingAssessment
    {
        Known,
        UncorrectableTypo,
        CorrectableTypo,
    }

    /// <summary>Identical punctuation or symbol characters in a row that count as junk when freshly introduced.</summary>
    private const int JunkRunLength = 4;

    /// <summary>Joined seam words shorter than this are too ambiguous to judge ("a" + "t").</summary>
    private const int MinimumSeamWordLength = 4;

    /// <summary>
    /// The text to show for a finished completion, or null when it must be suppressed. The spelling
    /// assessment is injected so the rule stays pure and the caller picks the backend.
    /// </summary>
    public static string? VisibleText(
        string precedingText, string completion, Func<string, SpellingAssessment> spellingAssessment)
    {
        if (completion.Length == 0 || IntroducesJunkPunctuationRun(precedingText, completion)) return null;

        var completionGraphemes = new GraphemeString(completion);
        string? prefix = CaretWordContext.UnfinishedWord(precedingText);
        if (prefix is not null && IsJoinedWord(prefix, completionGraphemes, spellingAssessment, out string? joined))
        {
            return joined;
        }

        // A generated boundary cannot silently abandon a fragment. Repeating that fragment as the
        // beginning of a longer new word catches "roo" + " room", even when a dictionary happens
        // to recognize "roo". A known completed word such as "car" may still start a phrase.
        if (prefix is not null && completionGraphemes[0].IsWhitespace)
        {
            int wordStart = completionGraphemes.PrefixLength(g => g.IsWhitespace);
            int letters = completionGraphemes.PrefixLength(g => g.IsLetter, wordStart);
            string firstWord = completion[
                completionGraphemes.Offset(wordStart)..completionGraphemes.Offset(wordStart + letters)];
            bool repeatsFragment = letters > GraphemeString.CountOf(prefix)
                && firstWord.ToLowerInvariant().StartsWith(prefix.ToLowerInvariant(), StringComparison.Ordinal);
            if (repeatsFragment || spellingAssessment(prefix) == SpellingAssessment.CorrectableTypo) return null;
        }

        if (SeamWordCandidate(precedingText, completion) is { } seamWord
            && spellingAssessment(seamWord) == SpellingAssessment.CorrectableTypo)
        {
            return null;
        }
        if (LeadingWordCandidate(precedingText, completionGraphemes) is { } leadingWord
            && spellingAssessment(leadingWord) == SpellingAssessment.CorrectableTypo)
        {
            return null;
        }
        return completion;
    }

    /// <summary>
    /// Examines only the lexical token touching the caret. False delegates code-like tokens to the
    /// ordinary continuation path. Otherwise <paramref name="visible"/> is the text to show, or
    /// null to suppress.
    /// </summary>
    private static bool IsJoinedWord(
        string prefix, GraphemeString completion, Func<string, SpellingAssessment> spellingAssessment,
        out string? visible)
    {
        visible = null;
        var first = completion[0];
        if (!first.IsLetter && !CaretWordContext.IsConnector(first)) return false;

        int endingCount = completion.PrefixLength(g => g.IsLetter || CaretWordContext.IsConnector(g));
        if (endingCount < completion.Count)
        {
            var following = completion[endingCount];
            if (following.IsNumber || following.Is('_')) return false;
        }

        string ending = completion.Prefix(endingCount);
        string word = prefix + ending;
        var assessment = spellingAssessment(word);
        var wordGraphemes = new GraphemeString(word);
        if (wordGraphemes[0].IsLowercase && wordGraphemes.Count >= MinimumSeamWordLength
            && assessment == SpellingAssessment.CorrectableTypo)
        {
            // Instruction-tuned models sometimes omit the separator after a complete word, so
            // "up" + "and ..." became "upand". Repair only whole words that are both known on
            // their own and whose join is a confirmed typo. Valid joins ("car" + "pet"), unknown
            // vocabulary and contraction fragments keep their original semantics.
            if (IsWholeWord(prefix) && IsWholeWord(ending)
                && spellingAssessment(prefix) == SpellingAssessment.Known
                && spellingAssessment(ending) == SpellingAssessment.Known)
            {
                visible = " " + completion.Text;
            }
            return true;
        }

        // Judge the complete joined word, not how many letters the writer has typed. Once "b" +
        // "uild" is a credible word, throwing away " a spaceship" would make the same phrase behave
        // differently at "b", "bu" and "bui". Unknown names and vocabulary get the ending only.
        visible = assessment == SpellingAssessment.Known ? completion.Text : ending;
        return true;
    }

    /// <summary>
    /// A word that separator repair may stand on its own. Letters, optionally joined by internal
    /// apostrophes or hyphens. A connector at either edge ("'t", "don'") marks a fragment of the
    /// word being spelled, so "don" + "'t" is never split.
    /// </summary>
    private static bool IsWholeWord(string token)
    {
        var graphemes = new GraphemeString(token);
        if (graphemes.Count == 0 || !graphemes[0].IsLetter || !graphemes[graphemes.Count - 1].IsLetter)
        {
            return false;
        }
        return graphemes.PrefixLength(g => g.IsLetter || CaretWordContext.IsConnector(g)) == graphemes.Count;
    }

    private static bool IntroducesJunkPunctuationRun(string precedingText, string completion)
    {
        var graphemes = new GraphemeString(completion);
        int runStart = 0;
        for (int index = 0; index < graphemes.Count; index++)
        {
            var current = graphemes[index];
            if (!current.Equals(graphemes[runStart])) runStart = index;
            if (index - runStart + 1 < JunkRunLength || (!current.IsPunctuation && !current.IsSymbol)) continue;

            // A run flush against the seam that continues a run of the same character the user
            // already has at the caret is an existing divider being extended, not fresh junk. It
            // must be a real preceding run of two or more. A sentence that merely ends in "." must
            // not exempt "...." from the completion.
            if (runStart == 0 && new GraphemeString(precedingText).SuffixLength(g => g.Equals(current)) >= 2)
            {
                continue;
            }
            return true;
        }
        return false;
    }

    /// <summary>
    /// The joined word across the caret seam when the mid-word rule applies, or null when a
    /// narrowing condition exempts it.
    /// </summary>
    private static string? SeamWordCandidate(string precedingText, string completion)
    {
        if (GraphemeString.LastOf(precedingText) is not { IsLetter: true }
            || GraphemeString.FirstOf(completion) is not { IsLetter: true })
        {
            return null;
        }

        var preceding = new GraphemeString(precedingText);
        var generated = new GraphemeString(completion);
        string seamWord = preceding.Suffix(preceding.SuffixLength(g => g.IsLetter))
            + generated.Prefix(generated.PrefixLength(g => g.IsLetter));
        var seam = new GraphemeString(seamWord);

        if (seam.Count < MinimumSeamWordLength) return null;
        // Capitalized joins are usually names or brands the dictionary cannot know.
        if (!seam[0].IsLowercase) return null;
        return ContainsCjk(seamWord) ? null : seamWord;
    }

    /// <summary>
    /// The first lexical word after boundary whitespace or punctuation, when it is one the spelling
    /// rule can judge. Apostrophes and hyphens between letters remain part of the word ("doesn't",
    /// "state-of-the-art") so the spell checker sees the same token the user sees.
    /// </summary>
    private static string? LeadingWordCandidate(string precedingText, GraphemeString completion)
    {
        int end = completion.Count;
        int wordStart = completion.PrefixLength(g => !g.IsLetter);
        if (wordStart == end) return null;

        // Digits make the token code-like or version-like, so the spelling rule does not apply.
        // The whole whitespace-delimited token is scanned so "ecrir2" is not misread as "ecrir".
        int tokenEnd = wordStart + completion.PrefixLength(g => !g.IsWhitespace, wordStart);
        if (completion.PrefixLength(g => !g.IsNumber) < tokenEnd) return null;

        // A bare apostrophe or hyphen can continue the word before the caret ("don" + "'t"). Any
        // other prefix character establishes a real boundary before the generated word.
        if (GraphemeString.LastOf(precedingText) is { IsLetter: true }
            && completion.PrefixLength(CaretWordContext.IsConnector) == wordStart)
        {
            return null;
        }

        int wordEnd = wordStart;
        while (wordEnd < end)
        {
            var character = completion[wordEnd];
            if (character.IsLetter)
            {
                wordEnd++;
                continue;
            }
            int next = wordEnd + 1;
            if (!CaretWordContext.IsConnector(character) || next >= end || !completion[next].IsLetter) break;
            wordEnd = next;
        }

        string word = completion.Text[completion.Offset(wordStart)..completion.Offset(wordEnd)];
        int letterCount = 0;
        bool startsLowercase = false;
        bool hasLaterUppercase = false;
        for (int index = wordStart; index < wordEnd; index++)
        {
            var character = completion[index];
            if (!character.IsLetter) continue;
            if (letterCount == 0) startsLowercase = character.IsLowercase;
            else if (character.IsUppercase) hasLaterUppercase = true;
            letterCount++;
        }
        if (!startsLowercase || hasLaterUppercase || ContainsCjk(word)) return null;
        return letterCount < MinimumSeamWordLength ? null : word;
    }

    /// <summary>
    /// Han, kana and Hangul ranges. These scripts have no space-delimited words, so a seam word is
    /// not a meaningful unit there and the spelling dictionaries do not cover them. Upstream keeps
    /// this list apart from the one in CaretWordContext. It adds radicals, CJK punctuation and the
    /// compatibility and halfwidth forms, and has no Thai, Lao or supplementary ideographs.
    /// </summary>
    private static bool ContainsCjk(string text)
    {
        foreach (var scalar in text.EnumerateRunes())
        {
            if (scalar.Value is (>= 0x2E80 and <= 0x9FFF) or (>= 0xAC00 and <= 0xD7AF)
                or (>= 0xF900 and <= 0xFAFF) or (>= 0xFF65 and <= 0xFF9F))
            {
                return true;
            }
        }
        return false;
    }
}
