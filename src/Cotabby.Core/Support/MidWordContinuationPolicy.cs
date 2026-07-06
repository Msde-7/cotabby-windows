namespace Cotabby.Core.Support;

/// <summary>
/// Decides whether the caret sits inside a word, in which case the first
/// generated token must continue that word rather than start a new one.
/// Direct port of the macOS <c>MidWordContinuationPolicy</c>.
/// </summary>
public static class MidWordContinuationPolicy
{
    public static bool ShouldForceContinuation(string precedingText, string trailingText)
    {
        if (precedingText.Length == 0 || trailingText.Length == 0) return false;
        return IsWordChar(precedingText[^1]) && IsWordChar(trailingText[0]);
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c);
}
