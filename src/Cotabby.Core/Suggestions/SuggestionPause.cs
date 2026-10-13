namespace Cotabby.Core.Suggestions;

/// <summary>A tray pause choice. The calendar math lives here so the menu only expresses intent.</summary>
public enum PauseDuration
{
    FifteenMinutes,
    ThirtyMinutes,
    OneHour,
    UntilTomorrow,
    Indefinitely,
}

/// <summary>
/// A temporary disable with an optional end. This stays separate from the global
/// enabled preference, which is the normal configuration the user chose.
/// </summary>
/// <param name="Until">When the pause ends, or null when it lasts until the user resumes.</param>
public sealed record PauseState(DateTimeOffset? Until)
{
    public static PauseState For(PauseDuration duration, DateTimeOffset now) => duration switch
    {
        PauseDuration.FifteenMinutes => new(now.AddMinutes(15)),
        PauseDuration.ThirtyMinutes => new(now.AddMinutes(30)),
        PauseDuration.OneHour => new(now.AddHours(1)),
        PauseDuration.UntilTomorrow => new(StartOfTomorrow(now)),
        _ => new((DateTimeOffset?)null),
    };

    public static string MenuLabel(PauseDuration duration) => duration switch
    {
        PauseDuration.FifteenMinutes => "Pause for 15 Minutes",
        PauseDuration.ThirtyMinutes => "Pause for 30 Minutes",
        PauseDuration.OneHour => "Pause for 1 Hour",
        PauseDuration.UntilTomorrow => "Pause Until Tomorrow",
        _ => "Pause Until I Turn It Back On",
    };

    public bool IsActive(DateTimeOffset now) => Until is null || Until > now;

    public string? StatusText(DateTimeOffset now)
    {
        if (!IsActive(now)) return null;
        if (Until is not { } until) return "Paused until enabled";
        if (until == StartOfTomorrow(now)) return "Paused until tomorrow";
        var local = until.ToLocalTime();
        return local.Date == now.ToLocalTime().Date
            ? $"Paused until {local:t}"
            : $"Paused until {local:MMM d}, {local:t}";
    }

    private static DateTimeOffset StartOfTomorrow(DateTimeOffset now)
    {
        var midnight = now.ToLocalTime().Date.AddDays(1);
        return new DateTimeOffset(midnight, TimeZoneInfo.Local.GetUtcOffset(midnight));
    }
}
