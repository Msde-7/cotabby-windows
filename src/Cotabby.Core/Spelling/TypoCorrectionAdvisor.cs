namespace Cotabby.Core.Spelling;

/// <summary>
/// What the coordinator should do about the word committed before the caret.
/// </summary>
/// <param name="Decision">The gate outcome, including the typo and its recased correction.</param>
/// <param name="Replacement">
/// The edit for the two correction actions, and null otherwise. It replaces the last
/// <c>DeletingUtf16Count</c> UTF-16 units of the text before the caret, which are the typo and
/// its delimiter.
/// </param>
public readonly record struct TypoAdvice(TypoGateDecision Decision, TypoCorrectionReplacement? Replacement = null);

/// <summary>
/// Runs the typo gate against the bundled dictionaries. One language is chosen from the enabled
/// dictionaries and the surrounding text, and that language both detects and corrects the typo.
/// </summary>
public sealed class TypoCorrectionAdvisor(
    SymSpellCorrector corrector, SpellingLanguageResolver resolver, ISpellingLexicon? lexicon = null)
{
    private readonly CurrentWordSpellChecker _spellChecker = new(lexicon ?? corrector);

    /// <summary>
    /// Decides between a normal completion, a suppressed one, an offered correction and an
    /// automatic fix. A cold dictionary or an ambiguous language reports no typo.
    ///
    /// The replacement is planned against <paramref name="precedingText"/>. When the edit is made
    /// later, as it is when the user accepts an offer, plan it again from the live text with
    /// <see cref="TypoCorrectionReplacementPlanner"/> so a keystroke in between cannot misplace it.
    /// </summary>
    public TypoAdvice Advise(
        string precedingText, IEnumerable<string> enabledDictionaryCodes, TypoGateSettings settings)
    {
        var languages = SpellingDictionaryCatalog.Languages(enabledDictionaryCodes);
        SpellingDictionaryLanguage? LanguageOf(string word) => resolver.Resolve(precedingText, word, languages);

        var decision = TypoGate.Resolve(
            precedingText,
            settings,
            isTypo: word => LanguageOf(word) is { } language && _spellChecker.IsTypo(word, language),
            bestCorrection: word => LanguageOf(word) is { } language ? corrector.BestCorrection(word, language) : null);
        if (decision.Word is null || decision.CorrectedWord is null) return new TypoAdvice(decision);

        // An automatic fix is reserved for a bare trailing space. An offer keeps any committed delimiter.
        var replacement = TypoCorrectionReplacementPlanner.Plan(
            precedingText,
            decision.Word,
            decision.CorrectedWord,
            requiresTrailingSpace: decision.Action == TypoGateAction.ApplyCorrection);
        return replacement is null ? new TypoAdvice(TypoGateDecision.Suppress) : new TypoAdvice(decision, replacement);
    }
}
