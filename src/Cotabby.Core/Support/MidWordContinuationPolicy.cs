namespace Cotabby.Core.Support;

/// <summary>
/// Decides whether the first generated token should be constrained to continue the current word.
/// The trigger is deliberately narrow. It fires only when the caret sits strictly inside a word,
/// with a word character on both sides. At a word end it returns false so ordinary next-word
/// predictions, where a leading space is wanted, are untouched.
/// </summary>
public static class MidWordContinuationPolicy
{
    public static bool ShouldForceContinuation(string precedingText, string trailingText) =>
        GraphemeString.LastOf(precedingText) is { } before && CaretTokenPosition.IsWordCharacter(before)
        && GraphemeString.FirstOf(trailingText) is { } after && CaretTokenPosition.IsWordCharacter(after);
}
