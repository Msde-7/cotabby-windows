using System.Globalization;
using Cotabby.Core.Macros;

namespace Cotabby.Core.Tests.Macros;

/// <summary>The clock is pinned to Thursday 2026-06-04 12:00 with the en-US culture.</summary>
public class DateMacroEvaluatorTests
{
    private static readonly DateTime Now = new(2026, 6, 4, 12, 0, 0);

    private static DateMacroEvaluator MakeEvaluator(string culture = "en-US") =>
        new(() => Now, CultureInfo.GetCultureInfo(culture));

    private static string? Insert(string query) => MakeEvaluator().Evaluate(query)?.InsertionText;

    [Fact]
    public void TodayUsesMediumFormat()
    {
        Assert.Equal("Jun 4, 2026", Insert("today"));
    }

    [Fact]
    public void DateStyleArguments()
    {
        Assert.Equal("2026-06-04", Insert("today(iso)"));
        Assert.Equal("June 4, 2026", Insert("today(long)"));
        Assert.Equal("6/4/2026", Insert("today(short)"));
        // An unrecognized argument falls back to the medium style rather than rejecting the macro.
        Assert.Equal("Jun 4, 2026", Insert("today(bogus)"));
        Assert.Equal("Jun 4, 2026", Insert("today()"));
    }

    [Fact]
    public void StyleArgumentAppliesToOffsetKeywords()
    {
        Assert.Equal("2026-06-05", Insert("tomorrow(iso)"));
        Assert.Equal("2026-06-05", Insert("+1d(iso)"));
    }

    [Fact]
    public void KeywordsAreCaseInsensitiveAndHaveWordAliases()
    {
        Assert.Equal("Jun 4, 2026", Insert("TODAY"));
        Assert.Equal("Jun 4, 2026", Insert("date"));
        Assert.Equal("12:00", Insert("time(24h)"));
    }

    [Fact]
    public void TomorrowAndYesterday()
    {
        Assert.Equal("Jun 5, 2026", Insert("tomorrow"));
        Assert.Equal("Jun 3, 2026", Insert("yesterday"));
    }

    [Fact]
    public void NextFridayFromThursday()
    {
        Assert.Equal("Jun 5, 2026", Insert("next-fri"));
    }

    [Fact]
    public void ThisWeekdayIncludesToday()
    {
        Assert.Equal("Jun 4, 2026", Insert("this-thu"));
    }

    [Fact]
    public void LastFridayFromThursday()
    {
        Assert.Equal("May 29, 2026", Insert("last-fri"));
    }

    [Fact]
    public void WeekdayMatchingTodayNextAndLastJumpAFullWeek()
    {
        // The clock is a Thursday. next and last are strictly after and before today.
        Assert.Equal("Jun 11, 2026", Insert("next-thu"));
        Assert.Equal("May 28, 2026", Insert("last-thu"));
    }

    [Fact]
    public void ThisWeekdayLooksForwardForEarlierWeekdays()
    {
        // this-wed on a Thursday is the coming Wednesday, not yesterday.
        Assert.Equal("Jun 10, 2026", Insert("this-wed"));
    }

    [Fact]
    public void FullWeekdayNames()
    {
        Assert.Equal("Jun 5, 2026", Insert("next friday"));
    }

    [Fact]
    public void UnknownWeekdayNameReturnsNull()
    {
        Assert.Null(Insert("next-funday"));
    }

    [Fact]
    public void RelativeOffsets()
    {
        Assert.Equal("Jun 7, 2026", Insert("+3d"));
        Assert.Equal("Jun 11, 2026", Insert("+1w"));
        Assert.Equal("May 30, 2026", Insert("-5d"));
        Assert.Equal("Jul 4, 2026", Insert("+1mo"));
        Assert.Equal("May 4, 2026", Insert("-1mo"));
        Assert.Equal("Jun 4, 2027", Insert("+1y"));
    }

    [Theory]
    [InlineData("+d")]
    [InlineData("+3")]
    [InlineData("+3x")]
    [InlineData("3d")]
    [InlineData("+")]
    public void MalformedRelativeOffsetsReturnNull(string query)
    {
        Assert.Null(Insert(query));
    }

    [Theory]
    [InlineData("+99999999d")]
    [InlineData("+99999y")]
    [InlineData("-99999y")]
    [InlineData("+99999999999999999999d")]
    public void OffsetsBeyondTheCalendarRangeReturnNull(string query)
    {
        Assert.Null(Insert(query));
    }

    [Fact]
    public void Now24HourArgument()
    {
        Assert.Equal("12:00", Insert("now(24h)"));
    }

    [Fact]
    public void NowDefaultsToTheCultureShortTime()
    {
        Assert.Equal("12:00 PM", Insert("now"));
    }

    [Fact]
    public void UnknownKeywordReturnsNull()
    {
        Assert.Null(Insert("someday"));
    }

    [Fact]
    public void ShortFormAliases()
    {
        Assert.Equal("Jun 4, 2026", Insert("tdy"));
        Assert.Equal("Jun 5, 2026", Insert("tmrw"));
        Assert.Equal("Jun 3, 2026", Insert("yest"));
        Assert.Equal(Insert("now"), Insert("rn"));
    }

    [Fact]
    public void WeekdaySeparatorVariants()
    {
        Assert.Equal("Jun 5, 2026", Insert("next fri"));
        Assert.Equal("Jun 5, 2026", Insert("nextfri"));
        Assert.Equal("Jun 5, 2026", Insert("next_fri"));
    }

    [Fact]
    public void SpelledOutRelativeUnits()
    {
        Assert.Equal("Jun 11, 2026", Insert("+1week"));
        Assert.Equal("Jun 6, 2026", Insert("+2days"));
    }

    [Fact]
    public void NoonAndMidnightWith24HourArgument()
    {
        Assert.Equal("12:00", Insert("noon(24h)"));
        Assert.Equal("00:00", Insert("midnight(24h)"));
    }

    [Fact]
    public void NoonDefaultsToTwelveHourClock()
    {
        Assert.Equal("12:00 PM", Insert("noon"));
        Assert.Equal("12:00 AM", Insert("midnight"));
    }

    [Fact]
    public void NoonAndMidnightIgnoreTheCurrentTimeOfDay()
    {
        var sut = new DateMacroEvaluator(() => new DateTime(2026, 6, 4, 17, 45, 30), CultureInfo.GetCultureInfo("en-US"));
        Assert.Equal("12:00", sut.Evaluate("noon(24h)")?.InsertionText);
        Assert.Equal("00:00", sut.Evaluate("midnight(24h)")?.InsertionText);
        Assert.Equal("17:45", sut.Evaluate("now(24h)")?.InsertionText);
    }

    [Fact]
    public void MiddayAliasResolvesToNoon()
    {
        Assert.Equal(Insert("noon(24h)"), Insert("midday(24h)"));
    }

    [Fact]
    public void DatetimeCombinesMediumDateWithShortTime()
    {
        Assert.Equal("Jun 4, 2026 12:00 PM", Insert("datetime"));
    }

    [Fact]
    public void DtAliasResolvesToDatetime()
    {
        Assert.Equal(Insert("datetime"), Insert("dt"));
    }

    [Fact]
    public void UnknownWeekdayPrefixReturnsNull()
    {
        // blah-fri carries a valid weekday token but no recognized this, next or last prefix,
        // so the weekday resolver must decline instead of guessing a direction.
        Assert.Null(Insert("blah-fri"));
    }

    [Fact]
    public void OutputFollowsTheInjectedCulture()
    {
        var sut = MakeEvaluator("de-DE");
        Assert.Equal("4. Juni 2026", sut.Evaluate("today(long)")?.InsertionText);
        Assert.Equal("04.06.2026", sut.Evaluate("today(short)")?.InsertionText);
        Assert.Equal("12:00", sut.Evaluate("now")?.InsertionText);
        Assert.Equal("4. Juli 2026", sut.Evaluate("+1mo(long)")?.InsertionText);
    }

    [Fact]
    public void LongAndMediumStylesNeverIncludeTheWeekday()
    {
        foreach (var culture in new[] { "en-US", "en-GB", "de-DE", "fr-FR", "es-ES", "ja-JP", "hu-HU" })
        {
            var info = CultureInfo.GetCultureInfo(culture);
            var sut = new DateMacroEvaluator(() => Now, info);
            var weekday = info.DateTimeFormat.GetDayName(DayOfWeek.Thursday);
            foreach (var query in new[] { "today", "today(long)" })
            {
                var text = sut.Evaluate(query)!.InsertionText;
                Assert.DoesNotContain(weekday, text, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("2026", text);
                Assert.Equal(text.Trim(' ', ','), text);
            }
        }
    }

    [Fact]
    public void IsoAnd24HourOutputIgnoreCulture()
    {
        using var scope = new CultureScope("ar-SA");
        var sut = new DateMacroEvaluator(() => Now);
        Assert.Equal("2026-06-04", sut.Evaluate("today(iso)")?.InsertionText);
        Assert.Equal("12:00", sut.Evaluate("now(24h)")?.InsertionText);
    }
}
