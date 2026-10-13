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
/// Code-like tokens and scripts without space-delimited words keep their normal path. The same
/// decision runs for streamed, final and cached candidates.
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

    public enum VerdictKind
    {
        Allow,
        JunkPunctuationRun,
        SeamMisspelling,
        LeadingWordMisspelling,
        AbandonedWord,
    }

    /// <summary>The verdict and, for the word-level kinds, the offending word.</summary>
    public readonly record struct Verdict(VerdictKind Kind, string? Word = null)
    {
        public static readonly Verdict Allow = new(VerdictKind.Allow);
        public static readonly Verdict JunkPunctuationRun = new(VerdictKind.JunkPunctuationRun);
        public static Verdict SeamMisspelling(string word) => new(VerdictKind.SeamMisspelling, word);
        public static Verdict LeadingWordMisspelling(string word) => new(VerdictKind.LeadingWordMisspelling, word);
        public static Verdict AbandonedWord(string word) => new(VerdictKind.AbandonedWord, word);
    }

    /// <summary>
    /// Streaming must not expose the first generated word until it is complete enough to assess.
    /// </summary>
    public enum StreamedLeadingWordVerdict
    {
        Wait,
        Allow,
        Suppress,
    }

    public enum PresentationKind
    {
        Wait,
        Suppress,
        Show,
    }

    /// <summary>
    /// One decision consumed by the streaming, final and cached-result paths. <c>WordOnly</c> records
    /// conservative lexical evidence. An unknown joined word can offer its ending without endorsing
    /// the following phrase.
    /// </summary>
    public readonly record struct PresentationDecision(
        PresentationKind Kind, Verdict Reason = default, string Text = "", bool WordOnly = false)
    {
        public static readonly PresentationDecision Wait = new(PresentationKind.Wait);
        public static PresentationDecision Suppress(Verdict reason) => new(PresentationKind.Suppress, reason);
        public static PresentationDecision Show(string text, bool wordOnly) =>
            new(PresentationKind.Show, Text: text, WordOnly: wordOnly);
    }

    /// <summary>Identical punctuation or symbol characters in a row that count as junk when freshly introduced.</summary>
    private const int JunkRunLength = 4;

    /// <summary>Joined seam words shorter than this are too ambiguous to judge ("a" + "t").</summary>
    private const int MinimumSeamWordLength = 4;

    /// <summary>
    /// Leading-word half of the streaming guard. Incomplete first words remain buffered, because
    /// testing a prefix such as "ecr" would create false positives.
    /// </summary>
    public static StreamedLeadingWordVerdict StreamedLeadingWord(
        string precedingText, string completion, Func<string, SpellingAssessment> spellingAssessment) =>
        Presentation(precedingText, completion, isFinal: false, spellingAssessment).Kind switch
        {
            PresentationKind.Wait => StreamedLeadingWordVerdict.Wait,
            PresentationKind.Suppress => StreamedLeadingWordVerdict.Suppress,
            _ => StreamedLeadingWordVerdict.Allow,
        };

    public static Verdict FinalVerdict(
        string precedingText, string completion, Func<string, SpellingAssessment> spellingAssessment)
    {
        var decision = Presentation(precedingText, completion, isFinal: true, spellingAssessment);
        return decision.Kind == PresentationKind.Suppress ? decision.Reason : Verdict.Allow;
    }

    /// <summary>
    /// The spelling assessment is injected so the rule stays pure and the caller picks the backend.
    /// </summary>
    public static PresentationDecision Presentation(
        string precedingText, string completion, bool isFinal,
        Func<string, SpellingAssessment> spellingAssessment)
    {
        if (IntroducesJunkPunctuationRun(precedingText, completion))
        {
            return PresentationDecision.Suppress(Verdict.JunkPunctuationRun);
        }
        if (completion.Length == 0) return PresentationDecision.Wait;

        var completionGraphemes = new GraphemeString(completion);
        string? prefix = CaretWordBoundary.UnfinishedWord(precedingText);
        if (prefix is not null
            && JoinedPresentation(prefix, completionGraphemes, isFinal, spellingAssessment) is { } joined)
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
            if (repeatsFragment || spellingAssessment(prefix) == SpellingAssessment.CorrectableTypo)
            {
                return PresentationDecision.Suppress(Verdict.AbandonedWord(prefix));
            }
        }

        var probe = ProbeLeadingWord(precedingText, completionGraphemes);
        if (!isFinal
            && (probe.Kind == ProbeKind.Incomplete || (probe.Kind == ProbeKind.Candidate && !probe.IsComplete)))
        {
            return PresentationDecision.Wait;
        }

        if (SeamWordCandidate(precedingText, completion) is { } seamWord
            && spellingAssessment(seamWord) == SpellingAssessment.CorrectableTypo)
        {
            return PresentationDecision.Suppress(Verdict.SeamMisspelling(seamWord));
        }
        if (probe.Kind == ProbeKind.Candidate
            && spellingAssessment(probe.Word) == SpellingAssessment.CorrectableTypo)
        {
            return PresentationDecision.Suppress(Verdict.LeadingWordMisspelling(probe.Word));
        }
        return PresentationDecision.Show(completion, wordOnly: false);
    }

    /// <summary>
    /// Examines only the lexical token touching the caret. Null delegates code-like tokens to the
    /// ordinary continuation path. Wait means a natural-language word is still arriving.
    /// </summary>
    private static PresentationDecision? JoinedPresentation(
        string prefix, GraphemeString completion, bool isFinal,
        Func<string, SpellingAssessment> spellingAssessment)
    {
        var first = completion[0];
        if (!first.IsLetter && !CaretWordBoundary.IsConnector(first)) return null;

        int endingCount = completion.PrefixLength(g => g.IsLetter || CaretWordBoundary.IsConnector(g));
        if (endingCount < completion.Count)
        {
            var following = completion[endingCount];
            if (following.IsNumber || following.Is('_')) return null;
        }
        if (!isFinal
            && (endingCount == completion.Count || CaretWordBoundary.IsConnector(completion[endingCount - 1])))
        {
            return PresentationDecision.Wait;
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
                return PresentationDecision.Show(" " + completion.Text, wordOnly: false);
            }
            return PresentationDecision.Suppress(Verdict.SeamMisspelling(word));
        }

        // Judge the complete joined word, not how many letters the writer has typed. Once "b" +
        // "uild" is a credible word, throwing away " a spaceship" would make the same phrase behave
        // differently at "b", "bu" and "bui". Unknown names and vocabulary get the ending only.
        bool wordOnly = assessment != SpellingAssessment.Known;
        return PresentationDecision.Show(wordOnly ? ending : completion.Text, wordOnly);
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
        return graphemes.PrefixLength(g => g.IsLetter || CaretWordBoundary.IsConnector(g)) == graphemes.Count;
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

    private enum ProbeKind
    {
        NotApplicable,
        Incomplete,
        Candidate,
    }

    private readonly record struct LeadingWordProbe(ProbeKind Kind, string Word = "", bool IsComplete = false);

    /// <summary>
    /// Finds the first lexical word after boundary whitespace or punctuation. Apostrophes and hyphens
    /// between letters remain part of the word ("doesn't", "state-of-the-art") so the spell checker
    /// sees the same token the user sees.
    /// </summary>
    private static LeadingWordProbe ProbeLeadingWord(string precedingText, GraphemeString completion)
    {
        var notApplicable = new LeadingWordProbe(ProbeKind.NotApplicable);
        var incomplete = new LeadingWordProbe(ProbeKind.Incomplete);
        int end = completion.Count;
        if (end == 0) return incomplete;

        int wordStart = completion.PrefixLength(g => !g.IsLetter);
        if (wordStart == end)
        {
            // Whitespace and opening punctuation may arrive before the first streamed word. Digits
            // make the token code-like or version-like, so the spelling rule does not apply.
            return completion.PrefixLength(g => g.IsWhitespace || g.IsPunctuation || g.IsSymbol) == end
                ? incomplete
                : notApplicable;
        }

        if (completion.PrefixLength(g => !g.IsNumber) < wordStart) return notApplicable;

        // A digit anywhere in the same whitespace-delimited token makes it code-like. Scan the
        // whole token so "ecrir2" is not misread as the natural-language word "ecrir".
        int tokenEnd = wordStart + completion.PrefixLength(g => !g.IsWhitespace, wordStart);
        if (wordStart + completion.PrefixLength(g => !g.IsNumber, wordStart) < tokenEnd) return notApplicable;

        // A bare apostrophe or hyphen can continue the word before the caret ("don" + "'t"). Any
        // other prefix character establishes a real boundary before the generated word.
        if (GraphemeString.LastOf(precedingText) is { IsLetter: true }
            && completion.PrefixLength(CaretWordBoundary.IsConnector) == wordStart)
        {
            return notApplicable;
        }

        int wordEnd = wordStart;
        bool endsInDanglingConnector = false;
        while (wordEnd < end)
        {
            var character = completion[wordEnd];
            if (character.IsLetter)
            {
                wordEnd++;
                continue;
            }
            int next = wordEnd + 1;
            bool isConnector = CaretWordBoundary.IsConnector(character);
            if (isConnector && next < end && completion[next].IsLetter)
            {
                wordEnd = next;
                continue;
            }
            endsInDanglingConnector = isConnector && next == end;
            break;
        }

        string word = completion.Text[completion.Offset(wordStart)..completion.Offset(wordEnd)];
        bool isComplete = wordEnd < end && !endsInDanglingConnector;
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
        if (!startsLowercase || hasLaterUppercase || ContainsCjk(word)) return notApplicable;
        if (letterCount < MinimumSeamWordLength) return isComplete ? notApplicable : incomplete;
        return new LeadingWordProbe(ProbeKind.Candidate, word, isComplete);
    }

    /// <summary>
    /// Han, kana and Hangul ranges. These scripts have no space-delimited words, so a seam word is
    /// not a meaningful unit there and the spelling dictionaries do not cover them.
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
