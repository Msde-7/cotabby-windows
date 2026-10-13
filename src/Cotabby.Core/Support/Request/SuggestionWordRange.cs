namespace Cotabby.Core.Support;

/// <summary>
/// Closed integer range a suggestion's word count should fall within. Shared by the curated presets
/// and a user-defined custom range.
/// </summary>
public readonly record struct SuggestionWordRange(int LowWords, int HighWords)
{
    /// <summary>Hard bounds for any range that drives generation.</summary>
    public const int MinimumWord = 1;
    public const int MaximumWord = 50;

    /// <summary>
    /// Clamps both ends to the hard bounds and keeps low at or below high, snapping high up to low.
    /// </summary>
    public static SuggestionWordRange Clamped(int low, int high)
    {
        int clampedLow = Math.Min(Math.Max(low, MinimumWord), MaximumWord);
        int clampedHigh = Math.Min(Math.Max(high, clampedLow), MaximumWord);
        return new SuggestionWordRange(clampedLow, clampedHigh);
    }

    public string CompactLabel => $"{LowWords}-{HighWords} w";

    public string PromptInstruction => $"Return only the next {LowWords} to {HighWords} words.";

    /// <summary>
    /// Maps a word range upper bound and a per-word factor onto a token budget, rounded up so the cap
    /// lands at or just past the upper word bound.
    /// </summary>
    public static int PredictionTokenBudget(int highWords, double tokensPerWord) =>
        (int)Math.Ceiling(highWords * tokensPerWord);
}

/// <summary>User-facing presets that bound how long one inline suggestion may be.</summary>
public enum SuggestionWordCountPreset
{
    TwoToFour,
    FourToSeven,
    SevenToTwelve,
    TwelveToTwenty,
}

public static class SuggestionWordCountPresetExtensions
{
    public static SuggestionWordRange Range(this SuggestionWordCountPreset preset) => preset switch
    {
        SuggestionWordCountPreset.TwoToFour => new(2, 4),
        SuggestionWordCountPreset.FourToSeven => new(4, 7),
        SuggestionWordCountPreset.SevenToTwelve => new(7, 12),
        _ => new(12, 20),
    };

    /// <summary>The stable persisted value, for example "2-4".</summary>
    public static string RawValue(this SuggestionWordCountPreset preset)
    {
        var range = preset.Range();
        return $"{range.LowWords}-{range.HighWords}";
    }

    public static string DisplayLabel(this SuggestionWordCountPreset preset) => $"{preset.RawValue()} words";
}
