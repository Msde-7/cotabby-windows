using System.Windows.Media;

namespace Cotabby.App.UI;

/// <summary>
/// Ghost text color presets. The id "auto" follows the system theme. Every other
/// id is its own "#RRGGBB" value, so stored settings never depend on a preset name.
/// </summary>
public static class GhostTextPalette
{
    public const string Auto = "auto";

    public static IReadOnlyList<(string Id, string Label)> Presets { get; } =
    [
        (Auto, "Automatic"),
        ("#3B82F6", "Blue"),
        ("#22C55E", "Green"),
        ("#A855F7", "Purple"),
        ("#F97316", "Orange"),
        ("#EC4899", "Pink"),
        ("#EF4444", "Red"),
        ("#EAB308", "Yellow"),
        ("#14B8A6", "Teal"),
        ("#06B6D4", "Cyan"),
        ("#6366F1", "Indigo"),
    ];

    /// <summary>The color for a stored id, or null when the theme default applies.</summary>
    public static Color? Resolve(string? id)
    {
        if (id is not { Length: 7 } || id[0] != '#') return null;
        try
        {
            return (Color)ColorConverter.ConvertFromString(id);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
