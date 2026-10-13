namespace Cotabby.Core.Spelling;

public enum TypoGateAction
{
    /// <summary>No actionable typo on the committed word. Run a normal completion.</summary>
    Proceed,

    /// <summary>
    /// The committed word looks misspelled and no correction can be shown. Hide the completion so
    /// it never piles onto a broken word.
    /// </summary>
    Suppress,

    /// <summary>Offer the correction as a replace-the-word suggestion.</summary>
    OfferCorrection,

    /// <summary>The user finished the word with Space and enabled automatic fixing. Apply the edit now.</summary>
    ApplyCorrection,
}

/// <summary>The gate outcome. <c>Word</c> is the typo and is set together with <c>CorrectedWord</c> for the two correction actions.</summary>
public readonly record struct TypoGateDecision(TypoGateAction Action, string? Word = null, string? CorrectedWord = null)
{
    public static TypoGateDecision Proceed => new(TypoGateAction.Proceed);

    public static TypoGateDecision Suppress => new(TypoGateAction.Suppress);

    public static TypoGateDecision OfferCorrection(string word, string correctedWord) =>
        new(TypoGateAction.OfferCorrection, word, correctedWord);

    public static TypoGateDecision ApplyCorrection(string word, string correctedWord) =>
        new(TypoGateAction.ApplyCorrection, word, correctedWord);
}

/// <summary>The user settings that shape the gate decision.</summary>
public readonly record struct TypoGateSettings(
    bool SuppressCompletionsOnTypo, bool OfferTypoCorrections, bool AutomaticallyFixTypos);

/// <summary>Pure decision rule for the typo gate that runs before each prediction.</summary>
public static class TypoGate
{
    /// <summary>
    /// Resolves the decision for the word committed at the end of <paramref name="precedingText"/>.
    /// Before a delimiter nothing is checked, so a partial word reaches the completion engine
    /// unchanged. Automatic fixing takes precedence only after a literal trailing space.
    /// </summary>
    public static TypoGateDecision Resolve(
        string precedingText,
        TypoGateSettings settings,
        Func<string, bool> isTypo,
        Func<string, string?> bestCorrection)
    {
        if (!settings.SuppressCompletionsOnTypo
            || CaretWordContext.CommittedWordIn(precedingText) is not { } current
            || !isTypo(current.Word))
        {
            return TypoGateDecision.Proceed;
        }
        if (bestCorrection(current.Word) is not { } corrected)
        {
            return TypoGateDecision.Suppress;
        }
        if (settings.AutomaticallyFixTypos && current.Delimiter == " ")
        {
            return TypoGateDecision.ApplyCorrection(current.Word, corrected);
        }
        return settings.OfferTypoCorrections
            ? TypoGateDecision.OfferCorrection(current.Word, corrected)
            : TypoGateDecision.Suppress;
    }
}
