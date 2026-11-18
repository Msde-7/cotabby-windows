using Cotabby.Core.Input;

namespace Cotabby.Core.Tests.Suggestions;

/// <summary>How often a visible suggestion is checked against the field, by the waits it asks of the clock.</summary>
public sealed class SuggestionCoordinatorWatchTests : CoordinatorHarness
{
    private static readonly TimeSpan Often = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan Seldom = TimeSpan.FromSeconds(1);

    private readonly ManualClock _clock;

    public SuggestionCoordinatorWatchTests() : this(new ManualClock())
    {
    }

    private SuggestionCoordinatorWatchTests(ManualClock clock) : base(clock: clock) => _clock = clock;

    private int WatchesAsked(TimeSpan every) => _clock.Timers.Count(t => t == every);

    [Fact]
    public async Task ASuggestionLeftAlone_IsCheckedLessOften_UntilTheNextKey()
    {
        Type("hello");
        await Shown(" world again");
        await Eventually(() => WatchesAsked(Often) >= 2, "the suggestion is checked every 250 ms");
        Assert.Equal(0, WatchesAsked(Seldom));

        // No key for a while.
        _clock.Advance(TimeSpan.FromSeconds(4));
        await Eventually(() => WatchesAsked(Seldom) >= 1, "the suggestion is checked every second");

        int often = WatchesAsked(Often);
        Hook.Press(Keys.Special(KeyKind.Other, virtualKey: 0xA0));
        await Eventually(() => WatchesAsked(Often) > often, "a key brings the checks back to every 250 ms");
        Assert.Equal(" world again", Overlay.Text);
    }
}
