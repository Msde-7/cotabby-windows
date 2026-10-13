using Cotabby.Core.Support;

namespace Cotabby.Core.Tests;

/// <summary>
/// Partials must neither replace a longer visible offer nor turn an unfinished hidden token into the
/// next acceptable word.
/// </summary>
public class StreamedGhostTextPolicyTests
{
    /// <summary>Buffers with "world" already visible.</summary>
    private static string Buffered(string text) =>
        StreamedGhostTextPolicy.CompletedBufferedPrediction(text, visibleCharacterCount: 5);

    [Theory]
    [InlineData("world ag", "world ")]
    [InlineData("world again tom", "world again ")]
    [InlineData("world again\n  tom", "world again\n  ")]
    [InlineData("world ag\t", "world ag\t")]
    public void HiddenTrailingFragmentWaitsForItsBoundaryBeforeBeingBuffered(string text, string expected)
    {
        Assert.Equal(expected, Buffered(text));
    }

    [Theory]
    [InlineData("world again.")]
    [InlineData("world again,")]
    [InlineData("world again!")]
    [InlineData("world (again)")]
    [InlineData("world \"again\"")]
    [InlineData("world 'again'")]
    [InlineData("world ‘again’")]
    public void PunctuationCompletesHiddenWordsWithoutNeedingWhitespace(string text)
    {
        Assert.Equal(text, Buffered(text));
    }

    [Theory]
    [InlineData("don't")]
    [InlineData("don'")]
    [InlineData("state-of-the-art")]
    [InlineData("state-")]
    [InlineData("item_2")]
    [InlineData("42")]
    [InlineData("(ag")]
    public void LexicalConnectorsAndIdentifiersDoNotCompleteHiddenTokens(string token)
    {
        Assert.Equal("world ", Buffered("world " + token));
    }

    [Theory]
    [InlineData("world ag", 7, "world a")]
    [InlineData("world ag", 8, "world ag")]
    [InlineData("world ag", 99, "world ag")]
    [InlineData("world ag", -1, "world ")]
    [InlineData("  ag", 0, "  ")]
    [InlineData("", 0, "")]
    // Boundaries count user characters, not UTF-16 units.
    [InlineData("🐈 café cafe\u0301", 6, "🐈 café ")]
    [InlineData("你好世界", 2, "你好")]
    public void VisibleCharactersAndWhitespaceSurviveBufferTrimming(string text, int visibleCount, string expected)
    {
        Assert.Equal(expected, StreamedGhostTextPolicy.CompletedBufferedPrediction(text, visibleCount));
    }

    [Theory]
    [InlineData(" wor", null, true)]
    [InlineData(" wor", "", true)]
    [InlineData("", null, false)]
    [InlineData("", " wor", false)]
    [InlineData(" world", " wor", true)]
    // An older, shorter partial arriving late is dropped, and equal text is redundant.
    [InlineData(" wor", " world", false)]
    [InlineData(" world", " world", false)]
    // A divergent rewrite waits for the final result.
    [InlineData(" worse idea", " world", false)]
    // A combining accent merges into the last visible character and adds none.
    [InlineData(" cafe\u0301", " cafe", false)]
    public void RenderableExtensionRequiresAStrictlyLongerPrefixExtension(
        string candidate, string? rendered, bool expected)
    {
        Assert.Equal(expected, StreamedGhostTextPolicy.IsRenderableExtension(candidate, rendered));
    }
}

public class SuggestionPresentationTimingTests
{
    private readonly record struct Identity(string Element, ulong FocusSequence);

    private static readonly Identity Field = new("field", 1);

    [Fact]
    public void LatestInputWinsAndOnlyFirstPresentationIsMeasured()
    {
        var timing = new SuggestionPresentationTiming<Identity>();
        timing.Begin(Field, "textMutation", 1);
        timing.Begin(Field, "acceptance", 2);
        var result = timing.Presented(Field, 2.25);
        Assert.Equal("acceptance", result?.InputKind);
        Assert.Equal(250, result?.Milliseconds);
        Assert.Null(timing.Presented(Field, 3));
    }

    [Fact]
    public void DifferentFocusRetiresMeasurement()
    {
        var timing = new SuggestionPresentationTiming<Identity>();
        timing.Begin(Field, "textMutation", 1);
        Assert.Null(timing.Presented(Field with { FocusSequence = 2 }, 2));
        Assert.Null(timing.Presented(Field, 3));
    }

    [Fact]
    public void ClearAndInvalidClockDoNotReportLatency()
    {
        var timing = new SuggestionPresentationTiming<Identity>();
        timing.Begin(Field, "textMutation", 2);
        Assert.Null(timing.Presented(Field, 1));
        timing.Begin(Field, "textMutation", 2);
        timing.Clear();
        Assert.Null(timing.Presented(Field, 3));
    }

    [Fact]
    public void NonFiniteTimestampsRetireMeasurement()
    {
        var timing = new SuggestionPresentationTiming<Identity>();
        timing.Begin(Field, "textMutation", double.PositiveInfinity);
        Assert.Null(timing.Presented(Field, 3));
        timing.Begin(Field, "textMutation", 1);
        Assert.Null(timing.Presented(Field, double.PositiveInfinity));
        Assert.Null(timing.Presented(Field, 3));
    }
}

public class TypingCadenceTests
{
    private static TypingCadence Typed(string letters, double interval, ulong identityKey = 1)
    {
        var cadence = new TypingCadence();
        for (int index = 0; index < letters.Length; index++)
        {
            cadence.Record(identityKey, letters[index].ToString(), index * interval);
        }
        return cadence;
    }

    [Fact]
    public void DisplayWaitsThroughBriefHesitationButNotAWordBoundary()
    {
        var cadence = Typed("sche", 0.1);
        Assert.Equal(0.1, cadence.RemainingDelay(1, "sche", 0.32), 3);
        Assert.Equal(0, cadence.RemainingDelay(1, "sche", 0.5));
        Assert.Equal(0, cadence.RemainingDelay(2, "sche", 0.32));
        cadence.Record(1, " ", 0.33);
        Assert.Equal(0, cadence.RemainingDelay(1, "schedule ", 0.34));
    }

    [Fact]
    public void QuietWindowTracksMedianRhythmWithinItsBounds()
    {
        // Steady 500 ms gaps would ask for 600 ms, and the window is capped at 220 ms.
        Assert.Equal(0.22, Typed("abc", 0.5).RemainingDelay(1, "abc", 1.0), 3);
        // Fast 30 ms gaps would ask for 36 ms, and the window never drops below 80 ms.
        Assert.Equal(0.08, Typed("abc", 0.03).RemainingDelay(1, "abc", 0.06), 3);
    }

    [Fact]
    public void SwitchingFieldsDiscardsThePreviousFieldsRhythm()
    {
        var cadence = Typed("abc", 0.5);
        cadence.Record(2, "x", 1.1);
        Assert.Equal(0, cadence.RemainingDelay(1, "abc", 1.1));
        // The new field starts without intervals, so only the default window applies.
        Assert.Equal(0.08, cadence.RemainingDelay(2, "x", 1.1), 3);
    }

    [Fact]
    public void NonFiniteTimestampsAreIgnoredAndNonWordPrefixesPayNoDelay()
    {
        var cadence = new TypingCadence();
        cadence.Record(1, "a", double.NaN);
        // A NaN timestamp must not even claim the field identity.
        Assert.Equal(0, cadence.RemainingDelay(1, "a", 0));
        cadence.Record(1, "a", 0);
        foreach (string prefix in new[] { "", "call(wor", "你好", "item2" })
        {
            Assert.Equal(0, cadence.RemainingDelay(1, prefix, 0.01));
        }
    }

    [Fact]
    public void LongPauseAndPasteDoNotBecomeTypingRhythm()
    {
        var cadence = new TypingCadence();
        cadence.Record(1, "s", 0);
        cadence.Record(1, "c", 5);
        Assert.Equal(0.08, cadence.RemainingDelay(1, "sc", 5), 3);
        cadence.Record(1, "pasted", 5.01);
        Assert.Equal(0, cadence.RemainingDelay(1, "pasted", 5.02));
    }
}
