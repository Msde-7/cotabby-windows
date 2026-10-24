namespace Cotabby.Core.Support;

/// <summary>
/// Bounds for persisted settings, applied when the file is read so a hand edit or an old
/// default cannot put a value outside what the settings window offers.
/// </summary>
public static class SettingsValues
{
    public const int MinDebounceMs = 10;
    public const int MaxDebounceMs = 500;

    public const double MinFadeSeconds = 0.05;
    public const double MaxFadeSeconds = 0.30;
    public const double DefaultFadeSeconds = 0.10;

    public const double MinOpacity = 0.3;
    public const double MaxOpacity = 1.0;

    public const double MinSizeMultiplier = 0.7;
    public const double MaxSizeMultiplier = 1.3;

    public const string AutoColor = "auto";

    public static int DebounceMs(int value) => Math.Clamp(value, MinDebounceMs, MaxDebounceMs);

    public static double FadeSeconds(double value) => Bounded(value, MinFadeSeconds, MaxFadeSeconds, DefaultFadeSeconds);

    public static double Opacity(double value) => Bounded(value, MinOpacity, MaxOpacity, MaxOpacity);

    public static double SizeMultiplier(double value) => Bounded(value, MinSizeMultiplier, MaxSizeMultiplier, 1.0);

    /// <summary>
    /// "#RRGGBB" in upper case for six hex digits, with or without the '#', and "auto" for
    /// anything else.
    /// </summary>
    public static string GhostColor(string? value)
    {
        string hex = (value ?? "").Trim().TrimStart('#');
        return hex.Length == 6 && hex.All(Uri.IsHexDigit) ? "#" + hex.ToUpperInvariant() : AutoColor;
    }

    private static double Bounded(double value, double min, double max, double fallback) =>
        double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
}
