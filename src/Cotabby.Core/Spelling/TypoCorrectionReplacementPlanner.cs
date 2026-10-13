namespace Cotabby.Core.Spelling;

/// <summary>
/// The exact edit that replaces one verified trailing typo. Delete
/// <paramref name="DeletingUtf16Count"/> UTF-16 units before the caret, then insert
/// <paramref name="ReplacementText"/>. Both cover the committing delimiter as well as the word.
/// </summary>
public readonly record struct TypoCorrectionReplacement(int DeletingUtf16Count, string ReplacementText);

/// <summary>
/// Builds a fail-closed replacement from the latest text before the caret. Accepted corrections
/// and automatic fixes both plan through here, so neither can delete a different word after the
/// user keeps typing.
/// </summary>
public static class TypoCorrectionReplacementPlanner
{
    public static TypoCorrectionReplacement? Plan(
        string precedingText, string expectedTypo, string correctedWord, bool requiresTrailingSpace)
    {
        var correction = correctedWord.Trim();
        if (correction.Length == 0 || correction == expectedTypo
            || CaretWordContext.CommittedWordIn(precedingText) is not { } live
            || live.Word != expectedTypo
            || (requiresTrailingSpace && live.Delimiter != " "))
        {
            return null;
        }

        return new TypoCorrectionReplacement(
            (expectedTypo + live.Delimiter).Length, correction + live.Delimiter);
    }
}
