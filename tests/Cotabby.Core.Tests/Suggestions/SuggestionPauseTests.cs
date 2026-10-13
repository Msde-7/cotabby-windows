using Cotabby.Core.Suggestions;
using Cotabby.Core.Tests.Macros;

namespace Cotabby.Core.Tests.Suggestions;

public sealed class SuggestionPauseTests
{
    private static readonly DateTimeOffset Morning = Local(2026, 6, 28, 9, 0);

    private static DateTimeOffset Local(int year, int month, int day, int hour, int minute) =>
        new(new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Local));

    [Fact]
    public void MenuLabel_IsPinnedInMenuOrder() =>
        Assert.Equal(
            [
                "Pause for 15 Minutes",
                "Pause for 30 Minutes",
                "Pause for 1 Hour",
                "Pause Until Tomorrow",
                "Pause Until I Turn It Back On",
            ],
            Enum.GetValues<PauseDuration>().Select(PauseState.MenuLabel));

    [Theory]
    [InlineData(PauseDuration.FifteenMinutes, 15)]
    [InlineData(PauseDuration.ThirtyMinutes, 30)]
    [InlineData(PauseDuration.OneHour, 60)]
    public void For_TimedDuration_EndsThatManyMinutesFromNow(PauseDuration duration, int minutes) =>
        Assert.Equal(Morning.AddMinutes(minutes), PauseState.For(duration, Morning).Until);

    [Fact]
    public void For_Indefinitely_HasNoEnd() =>
        Assert.Null(PauseState.For(PauseDuration.Indefinitely, Morning).Until);

    [Fact]
    public void For_UntilTomorrow_EndsAtTheNextLocalMidnight() =>
        Assert.Equal(
            Local(2026, 6, 29, 0, 0),
            PauseState.For(PauseDuration.UntilTomorrow, Local(2026, 6, 28, 22, 30)).Until);

    [Fact]
    public void IsActive_TimedPause_EndsExactlyAtItsExpiration()
    {
        var state = new PauseState(Morning);

        Assert.True(state.IsActive(Morning.AddMilliseconds(-1)));
        Assert.False(state.IsActive(Morning));
    }

    [Fact]
    public void IsActive_IndefinitePause_NeverEnds() =>
        Assert.True(PauseState.For(PauseDuration.Indefinitely, Morning).IsActive(DateTimeOffset.MaxValue));

    [Fact]
    public void StatusText_ChoosesItsWordingByPauseKind()
    {
        using var culture = new CultureScope("");

        Assert.Equal("Paused until enabled", PauseState.For(PauseDuration.Indefinitely, Morning).StatusText(Morning));
        Assert.Equal("Paused until tomorrow", PauseState.For(PauseDuration.UntilTomorrow, Morning).StatusText(Morning));
        Assert.Equal("Paused until 10:00", new PauseState(Morning.AddHours(1)).StatusText(Morning));
        Assert.Equal("Paused until Jun 30, 09:00", new PauseState(Morning.AddDays(2)).StatusText(Morning));
    }

    [Fact]
    public void StatusText_IsNullOnceThePauseExpired() =>
        Assert.Null(new PauseState(Morning).StatusText(Morning));
}
