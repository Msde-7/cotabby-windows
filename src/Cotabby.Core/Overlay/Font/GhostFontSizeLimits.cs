namespace Cotabby.Core.Overlay.Font;

/// <summary>
/// The legibility backstop for ghost text and the user's Smallest and Largest Ghost Text settings.
/// Mirrors the macOS <c>GhostFontSizeLimits</c>.
/// </summary>
public static class GhostFontSizeLimits
{
    /// <summary>Below this the ghost is unreadable, whatever the host reports or the user sets.</summary>
    public const double AbsoluteMinimum = 9;

    public const double DefaultFloor = 11;
    public const double MinimumFloor = 9;
    public const double MaximumFloor = 24;

    public const double DefaultCeiling = 48;
    public const double MinimumCeiling = 16;
    public const double MaximumCeiling = 96;

    /// <summary>
    /// <paramref name="size"/> held inside the floor and ceiling, with the backstop applied last.
    /// An inverted range applies the ceiling first so the floor wins.
    /// </summary>
    public static double Clamped(double size, double floor, double ceiling)
    {
        double clamped = size;
        if (ceiling > 0) clamped = Math.Min(clamped, ceiling);
        if (floor > 0) clamped = Math.Max(clamped, floor);
        return Math.Max(AbsoluteMinimum, clamped);
    }

    /// <summary>A floor and ceiling inside their ranges, with the floor never above the ceiling.</summary>
    public static (double Floor, double Ceiling) Normalized(double floor, double ceiling)
    {
        floor = double.IsFinite(floor) ? Math.Clamp(floor, MinimumFloor, MaximumFloor) : DefaultFloor;
        ceiling = double.IsFinite(ceiling) ? Math.Clamp(ceiling, MinimumCeiling, MaximumCeiling) : DefaultCeiling;
        return (floor, Math.Max(ceiling, floor));
    }
}
