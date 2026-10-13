namespace Cotabby.Core.Support;

/// <summary>Closed integer range a suggestion's word count should fall within.</summary>
public readonly record struct SuggestionWordRange(int LowWords, int HighWords)
{
    /// <summary>
    /// Maps a word range upper bound and a per-word factor onto a token budget, rounded up so the cap
    /// lands at or just past the upper word bound.
    /// </summary>
    public static int PredictionTokenBudget(int highWords, double tokensPerWord) =>
        (int)Math.Ceiling(highWords * tokensPerWord);
}
