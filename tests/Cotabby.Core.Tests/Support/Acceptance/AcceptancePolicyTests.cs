using Cotabby.Core.Support;

namespace Cotabby.Core.Tests;

public class DoubleTapAcceptanceStateTests
{
    private static readonly DoubleTapAcceptanceState.SessionToken Session = new(7, " world and more");

    private static DoubleTapAcceptanceState Recorded()
    {
        var state = new DoubleTapAcceptanceState();
        state.RecordWordAccept(Session, 10.0);
        return state;
    }

    [Fact]
    public void SecondPressWithinWindowOnSameSessionIsDoubleTap()
    {
        Assert.True(Recorded().ConsumeDoubleTap(Session, 10.2));
    }

    [Fact]
    public void SecondPressAfterWindowIsNotDoubleTap()
    {
        Assert.False(Recorded().ConsumeDoubleTap(Session, 10.0 + DoubleTapAcceptanceState.Window + 0.01));
    }

    [Fact]
    public void PressOnDifferentSessionIsNotDoubleTap()
    {
        Assert.False(Recorded().ConsumeDoubleTap(Session with { Generation = 8 }, 10.1));
    }

    [Fact]
    public void FirstPressWithoutRecordedAcceptIsNotDoubleTap()
    {
        Assert.False(new DoubleTapAcceptanceState().ConsumeDoubleTap(Session, 10.0));
    }

    [Fact]
    public void ConsumingEndsThePairSoATripleTapIsNotASecondDoubleTap()
    {
        var state = Recorded();
        Assert.True(state.ConsumeDoubleTap(Session, 10.1));
        Assert.False(state.ConsumeDoubleTap(Session, 10.2));
    }

    [Fact]
    public void MissedDoubleTapAlsoClearsThePendingPress()
    {
        var state = Recorded();
        Assert.False(state.ConsumeDoubleTap(Session, 11.0));
        Assert.False(state.HasPendingPress);
    }

    [Fact]
    public void ResetCancelsThePendingPress()
    {
        var state = Recorded();
        state.Reset();
        Assert.False(state.ConsumeDoubleTap(Session, 10.1));
    }

    [Fact]
    public void ClockGoingBackwardsIsNotDoubleTap()
    {
        Assert.False(Recorded().ConsumeDoubleTap(Session, 9.9));
    }
}

public class InsertedTextAdvanceTests
{
    /// <summary>A stand-in host font where every UTF-16 unit is six units wide.</summary>
    private static readonly InsertedTextAdvance.HostFontMeasurer SixPerUnit = text => text.Length * 6.0;

    [Fact]
    public void WidthUsesTheHostFontAndCountsLeadingWhitespace()
    {
        Assert.Equal(36, InsertedTextAdvance.Width(" world", SixPerUnit));
        Assert.True(InsertedTextAdvance.Width(" world", SixPerUnit) > InsertedTextAdvance.Width("world", SixPerUnit));
    }

    [Fact]
    public void WidthPrefersTheMeasuredRunCharWidthOverTheResolvedFont()
    {
        Assert.Equal(7.5 * 6, InsertedTextAdvance.Width(" world", observedCharWidth: 7.5, SixPerUnit));
        // Without the measurement the resolved font carries the estimate.
        Assert.Equal(36, InsertedTextAdvance.Width(" world", observedCharWidth: null, SixPerUnit));
        Assert.Null(InsertedTextAdvance.Width(" world", observedCharWidth: null, null));
        Assert.Null(InsertedTextAdvance.Width("", observedCharWidth: 7.5, SixPerUnit));
    }

    [Fact]
    public void ObservedCharWidthScalesByUtf16LengthAndIgnoresNonPositiveWidths()
    {
        // A surrogate pair counts as two measured units.
        Assert.Equal(10, InsertedTextAdvance.Width("🎉", observedCharWidth: 5, null));
        foreach (double unusable in new[] { 0.0, -3.0 })
        {
            Assert.Equal(36, InsertedTextAdvance.Width(" world", unusable, SixPerUnit));
            Assert.Null(InsertedTextAdvance.Width(" world", unusable, null));
        }
    }

    [Fact]
    public void WidthRefusesUnusableInputs()
    {
        Assert.Null(InsertedTextAdvance.Width("", SixPerUnit));
        Assert.Null(InsertedTextAdvance.Width(" world", null));
        Assert.Null(InsertedTextAdvance.Width(" world", _ => 0));
        Assert.Null(InsertedTextAdvance.Width(" world", _ => double.NaN));
    }
}

public class InsertionSafetyGateTests
{
    [Theory]
    [InlineData("hello there")]
    // Closing a bracket or ending a sentence is a legitimate inline completion.
    [InlineData(")")]
    [InlineData(".")]
    [InlineData("first line\nsecond line")]
    [InlineData("  indented")]
    [InlineData("🎉 café")]
    public void LegitimateCompletionsAreSafe(string completion)
    {
        Assert.True(InsertionSafetyGate.IsSafeToInsert(completion));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\n")]
    [InlineData("\u00A0")]
    [InlineData("ab\uFFFDcd")]
    [InlineData("a\tb")]
    [InlineData("a\u001Bb")]
    [InlineData("a\u007Fb")]
    // Only the line feed is exempt, so a carriage return is refused.
    [InlineData("a\rb")]
    public void JunkCompletionsAreUnsafe(string completion)
    {
        Assert.False(InsertionSafetyGate.IsSafeToInsert(completion));
    }
}

/// <summary>
/// Paste is opt-in and reserved for long or multi-line chunks. Everything else stays on the
/// clipboard-free keystroke path.
/// </summary>
public class InsertionStrategySelectorTests
{
    private const int Threshold = InsertionStrategySelector.PasteCharacterThreshold;

    private static string Repeated(string unit, int count) => string.Concat(Enumerable.Repeat(unit, count));

    [Theory]
    [InlineData("hi")]
    [InlineData("a\nb")]
    public void PasteDisabledAlwaysKeystroke(string chunk)
    {
        Assert.Equal(InsertionStrategy.Keystroke, InsertionStrategySelector.Strategy(chunk, pasteEnabled: false));
        Assert.Equal(
            InsertionStrategy.Keystroke, InsertionStrategySelector.Strategy(new string('a', 200), pasteEnabled: false));
    }

    [Fact]
    public void PasteEnabledShortSingleLineKeystrokes()
    {
        Assert.Equal(
            InsertionStrategy.Keystroke, InsertionStrategySelector.Strategy("a short completion", pasteEnabled: true));
    }

    [Fact]
    public void PasteEnabledMultiLinePastes()
    {
        Assert.Equal(
            InsertionStrategy.Paste, InsertionStrategySelector.Strategy("line one\nline two", pasteEnabled: true));
    }

    [Fact]
    public void PasteEnabledLongChunkPastesAndJustBelowThresholdKeystrokes()
    {
        Assert.Equal(
            InsertionStrategy.Paste, InsertionStrategySelector.Strategy(new string('a', Threshold), pasteEnabled: true));
        Assert.Equal(
            InsertionStrategy.Keystroke,
            InsertionStrategySelector.Strategy(new string('a', Threshold - 1), pasteEnabled: true));
    }

    [Fact]
    public void ThresholdCountsUserCharactersNotUtf16Units()
    {
        // Each emoji is two UTF-16 units.
        Assert.Equal(
            InsertionStrategy.Keystroke,
            InsertionStrategySelector.Strategy(Repeated("🐈", Threshold - 1), pasteEnabled: true));
        Assert.Equal(
            InsertionStrategy.Paste, InsertionStrategySelector.Strategy(Repeated("🐈", Threshold), pasteEnabled: true));
    }

    [Theory]
    [InlineData("a\rb")]
    [InlineData("a\r\nb")]
    [InlineData("a\u2028b")]
    public void AnyNewlineCharacterCountsAsMultiLine(string chunk)
    {
        Assert.Equal(InsertionStrategy.Paste, InsertionStrategySelector.Strategy(chunk, pasteEnabled: true));
    }
}
