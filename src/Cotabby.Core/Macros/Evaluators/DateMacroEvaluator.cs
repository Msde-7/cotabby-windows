using System.Globalization;
using System.Text.RegularExpressions;

namespace Cotabby.Core.Macros;

/// <summary>
/// Date and time macros. Covers <c>/today</c>, <c>/now</c>, <c>/datetime</c>,
/// <c>/tomorrow</c>, <c>/yesterday</c>, <c>/noon</c>, <c>/midnight</c>, weekday navigation
/// (<c>/next-fri</c>, <c>/next fri</c>, <c>/nextfri</c>) and relative offsets (<c>/+3d</c>,
/// <c>/-5d</c>, <c>/+2w</c>, <c>/+1mo</c>, <c>/+1y</c>, <c>/+1week</c>). An optional argument
/// tunes the output, as in <c>/today(iso)</c>, <c>/today(long)</c>, <c>/today(short)</c> and
/// <c>/now(24h)</c>.
/// </summary>
/// <remarks>
/// Output follows the culture. The input keywords are fixed English, like code.
/// </remarks>
public sealed partial class DateMacroEvaluator : IMacroEvaluator
{
    private readonly Func<DateTime> _now;
    private readonly CultureInfo _culture;
    private readonly Calendar _calendar;
    private readonly string _longPattern;
    private readonly string _mediumPattern;

    /// <param name="now">Local wall-clock time.</param>
    /// <param name="culture">Culture for rendered output. Defaults to the current culture.</param>
    public DateMacroEvaluator(Func<DateTime> now, CultureInfo? culture = null)
    {
        _now = now;
        _culture = culture ?? CultureInfo.CurrentCulture;
        _calendar = _culture.DateTimeFormat.Calendar;
        // .NET has no long or medium date style without the weekday, so derive both.
        _longPattern = WeekdayInPattern().Replace(_culture.DateTimeFormat.LongDatePattern, "").Trim();
        _mediumPattern = _longPattern.Replace("MMMM", "MMM");
    }

    public MacroResult? Evaluate(string query)
    {
        var (rawBase, argument) = SplitArgument(query.ToLowerInvariant());
        var keyword = CanonicalBase(rawBase);

        MacroResult? Date(DateTime? date) => date is { } value ? new MacroResult(FormatDate(value, argument)) : null;
        MacroResult Time(DateTime time) => new(FormatTime(time, use24Hour: argument == "24h"));

        if (RelativeDate(keyword) is { } relative) return Date(relative);

        switch (keyword)
        {
            case "today" or "date":
                return Date(_now());
            case "tomorrow":
                return Date(AddDays(1));
            case "yesterday":
                return Date(AddDays(-1));
            case "now" or "time":
                return Time(_now());
            case "datetime":
                var now = _now();
                return new MacroResult($"{now.ToString(_mediumPattern, _culture)} {FormatTime(now, use24Hour: false)}");
            case "noon":
                return Time(_now().Date.AddHours(12));
            case "midnight":
                return Time(_now().Date);
            default:
                return Date(WeekdayDate(keyword));
        }
    }

    private DateTime? AddDays(int days) => Offset(days, (calendar, date, n) => calendar.AddDays(date, n));

    // Returns null when the shifted date falls outside the calendar's range.
    private DateTime? Offset(int value, Func<Calendar, DateTime, int, DateTime> add)
    {
        try
        {
            return add(_calendar, _now(), value);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Parses relative offsets like <c>+3d</c>, <c>-5d</c>, <c>+2w</c>, <c>+1mo</c>, <c>+1y</c>.</summary>
    private DateTime? RelativeDate(string keyword)
    {
        if (keyword.Length == 0 || keyword[0] is not ('+' or '-')) return null;
        int end = 1;
        while (end < keyword.Length && char.IsNumber(keyword[end])) end++;
        if (MacroNumber.ParseInt(keyword.AsSpan(1, end - 1)) is not { } magnitude) return null;
        int value = keyword[0] == '-' ? -magnitude : magnitude;

        return keyword[end..] switch
        {
            "d" or "day" or "days" => AddDays(value),
            "w" or "wk" or "wks" or "week" or "weeks" => Offset(value, (calendar, date, n) => calendar.AddWeeks(date, n)),
            "mo" or "month" or "months" => Offset(value, (calendar, date, n) => calendar.AddMonths(date, n)),
            "y" or "yr" or "yrs" or "year" or "years" => Offset(value, (calendar, date, n) => calendar.AddYears(date, n)),
            _ => null,
        };
    }

    /// <summary>
    /// Resolves <c>next-&lt;weekday&gt;</c>, <c>this-&lt;weekday&gt;</c> and <c>last-&lt;weekday&gt;</c>.
    /// <c>next</c> is strictly after today, <c>this</c> includes today, and <c>last</c> is
    /// strictly before today.
    /// </summary>
    private DateTime? WeekdayDate(string keyword)
    {
        var parts = keyword.Split('-', 2);
        if (parts.Length != 2 || !Weekdays.TryGetValue(parts[1], out var target)) return null;
        int forward = ((int)target - (int)_calendar.GetDayOfWeek(_now()) + 7) % 7;
        int? delta = parts[0] switch
        {
            "this" => forward,
            "next" => forward == 0 ? 7 : forward,
            "last" => forward == 0 ? -7 : forward - 7,
            _ => null,
        };
        return delta is { } days ? AddDays(days) : null;
    }

    private string FormatDate(DateTime date, string? style) => style switch
    {
        "iso" => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        "long" => date.ToString(_longPattern, _culture),
        "short" => date.ToString("d", _culture),
        _ => date.ToString(_mediumPattern, _culture),
    };

    // A fixed 24-hour format overrides the culture's 12 or 24 hour preference.
    private string FormatTime(DateTime time, bool use24Hour) =>
        use24Hour ? time.ToString("HH:mm", CultureInfo.InvariantCulture) : time.ToString("t", _culture);

    private static (string Base, string? Argument) SplitArgument(string text)
    {
        int open = text.IndexOf('(');
        if (open < 0 || !text.EndsWith(')')) return (text, null);
        var argument = text[(open + 1)..^1];
        return (text[..open], argument.Length == 0 ? null : argument);
    }

    /// <summary>
    /// Applies the short form aliases, then rewrites a <c>next</c>, <c>this</c> or <c>last</c>
    /// weekday prefix written with a space or no separator into the dash form.
    /// </summary>
    private static string CanonicalBase(string keyword)
    {
        if (BaseAliases.TryGetValue(keyword, out var alias)) return alias;
        foreach (var prefix in WeekdayPrefixes)
        {
            if (!keyword.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var rest = keyword[prefix.Length..].TrimStart(' ', '-', '_');
            if (rest.Length > 0) return $"{prefix}-{rest}";
        }
        return keyword;
    }

    private static readonly string[] WeekdayPrefixes = ["next", "this", "last"];

    private static readonly Dictionary<string, string> BaseAliases = new()
    {
        ["tdy"] = "today", ["tod"] = "today", ["tody"] = "today", ["2day"] = "today",
        ["tmr"] = "tomorrow", ["tmrw"] = "tomorrow", ["tmw"] = "tomorrow", ["tom"] = "tomorrow", ["tomo"] = "tomorrow",
        ["tomorow"] = "tomorrow", ["2moro"] = "tomorrow", ["2mrw"] = "tomorrow",
        ["yest"] = "yesterday", ["yday"] = "yesterday", ["ystdy"] = "yesterday", ["yesty"] = "yesterday", ["yesterdy"] = "yesterday",
        ["rn"] = "now", ["rightnow"] = "now", ["atm"] = "now",
        ["midday"] = "noon", ["noontime"] = "noon", ["midnite"] = "midnight",
        ["dt"] = "datetime",
    };

    private static readonly Dictionary<string, DayOfWeek> Weekdays = new()
    {
        ["sun"] = DayOfWeek.Sunday, ["sunday"] = DayOfWeek.Sunday,
        ["mon"] = DayOfWeek.Monday, ["monday"] = DayOfWeek.Monday,
        ["tue"] = DayOfWeek.Tuesday, ["tues"] = DayOfWeek.Tuesday, ["tuesday"] = DayOfWeek.Tuesday,
        ["wed"] = DayOfWeek.Wednesday, ["weds"] = DayOfWeek.Wednesday, ["wednesday"] = DayOfWeek.Wednesday,
        ["thu"] = DayOfWeek.Thursday, ["thur"] = DayOfWeek.Thursday, ["thurs"] = DayOfWeek.Thursday, ["thursday"] = DayOfWeek.Thursday,
        ["fri"] = DayOfWeek.Friday, ["friday"] = DayOfWeek.Friday,
        ["sat"] = DayOfWeek.Saturday, ["saturday"] = DayOfWeek.Saturday,
    };

    // The weekday specifier of a long date pattern together with its adjoining separator.
    [GeneratedRegex(@"^d{3,4}[\s,.]*|[\s,]*d{3,4}")]
    private static partial Regex WeekdayInPattern();
}
