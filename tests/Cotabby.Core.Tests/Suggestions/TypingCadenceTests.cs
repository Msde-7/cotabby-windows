using Cotabby.Core.Suggestions;

namespace Cotabby.Core.Tests.Suggestions;

public sealed class TypingCadenceTests
{
    private static TypingCadence Typed(string field, string letters, double gap, double start = 0)
    {
        var cadence = new TypingCadence();
        for (int i = 0; i < letters.Length; i++) cadence.Record(field, letters[i].ToString(), start + i * gap);
        return cadence;
    }

    [Fact]
    public void DisplayWaitsThroughABriefHesitationButNotAWordBoundary()
    {
        var cadence = Typed("1", "sche", 0.1);

        Assert.Equal(0.1, cadence.RemainingDelay("1", "sche", 0.32), 3);
        Assert.Equal(0, cadence.RemainingDelay("1", "sche", 0.5));
        Assert.Equal(0, cadence.RemainingDelay("2", "sche", 0.32));
        cadence.Record("1", " ", 0.33);
        Assert.Equal(0, cadence.RemainingDelay("1", "schedule ", 0.34));
    }

    [Fact]
    public void TheQuietWindowTracksTheMedianRhythmWithinItsBounds()
    {
        // Steady 500 ms gaps would ask for 600 ms. The window is capped at 220 ms.
        Assert.Equal(0.22, Typed("1", "abc", 0.5).RemainingDelay("1", "abc", 1.0), 3);

        // 30 ms gaps would ask for 36 ms. The window never drops below 80 ms.
        Assert.Equal(0.08, Typed("1", "abc", 0.03).RemainingDelay("1", "abc", 0.06), 3);
    }

    [Fact]
    public void SwitchingFieldsDiscardsThePreviousFieldsRhythm()
    {
        var cadence = Typed("1", "abc", 0.5);

        cadence.Record("2", "x", 1.1);

        Assert.Equal(0, cadence.RemainingDelay("1", "abc", 1.1));
        // The new field has no intervals yet, so only the brief-hesitation window applies.
        Assert.Equal(0.08, cadence.RemainingDelay("2", "x", 1.1), 3);
    }

    [Fact]
    public void NonFiniteTimesAreIgnoredAndNonWordPrefixesPayNoDelay()
    {
        var cadence = new TypingCadence();
        cadence.Record("1", "a", double.NaN);
        Assert.Equal(0, cadence.RemainingDelay("1", "a", 0));

        cadence.Record("1", "a", 0);
        foreach (var prefix in new[] { "", "call(wor", "你好", "item2" })
        {
            Assert.Equal(0, cadence.RemainingDelay("1", prefix, 0.01));
        }
    }

    [Fact]
    public void ALongPauseAndAPasteDoNotBecomeTypingRhythm()
    {
        var cadence = new TypingCadence();
        cadence.Record("1", "s", 0);
        cadence.Record("1", "c", 5);
        Assert.Equal(0.08, cadence.RemainingDelay("1", "sc", 5), 3);

        cadence.Record("1", "pasted", 5.01);
        Assert.Equal(0, cadence.RemainingDelay("1", "pasted", 5.02));
    }
}
