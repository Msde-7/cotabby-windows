using System.Collections.Generic;
using System.Globalization;
using System.Windows.Media;

namespace Cotabby.App.UI;

/// <summary>
/// Color presets for the ghost text. Mirrors the macOS port's settings palette
/// — the special id <c>"auto"</c> means "track the system light/dark theme",
/// everything else is a fixed RGB.
/// </summary>
public static class GhostTextPalette
{
    public sealed record Choice(string Id, string Label, Color? Color = null)
    {
        public override string ToString() => Label;
    }

    // The exact swatch set (and order) of the macOS GhostTextColorPreset list.
    public static readonly IReadOnlyList<Choice> Choices =
    [
        new Choice("auto",   "Automatic"),
        new Choice("blue",   "Blue",   Color.FromRgb(0x3B, 0x82, 0xF6)),
        new Choice("green",  "Green",  Color.FromRgb(0x22, 0xC5, 0x5E)),
        new Choice("purple", "Purple", Color.FromRgb(0xA8, 0x55, 0xF7)),
        new Choice("orange", "Orange", Color.FromRgb(0xF9, 0x73, 0x16)),
        new Choice("pink",   "Pink",   Color.FromRgb(0xEC, 0x48, 0x99)),
        new Choice("red",    "Red",    Color.FromRgb(0xEF, 0x44, 0x44)),
        new Choice("yellow", "Yellow", Color.FromRgb(0xEA, 0xB3, 0x08)),
        new Choice("teal",   "Teal",   Color.FromRgb(0x14, 0xB8, 0xA6)),
        new Choice("cyan",   "Cyan",   Color.FromRgb(0x06, 0xB6, 0xD4)),
        new Choice("indigo", "Indigo", Color.FromRgb(0x63, 0x66, 0xF1)),
    ];

    /// <summary>Alias kept for settings-UI binding symmetry with the design spec.</summary>
    public static IReadOnlyList<Choice> Presets => Choices;

    /// <summary>
    /// Resolve a stored id (or "#RRGGBB" hex) to a Color, falling back to null
    /// when the id is "auto" or unparseable — callers should then apply the
    /// system-theme default.
    /// </summary>
    public static Color? Resolve(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        if (id.Equals("auto", System.StringComparison.OrdinalIgnoreCase)) return null;
        foreach (var c in Choices)
        {
            if (c.Color is { } col && c.Id == id) return col;
        }
        if (id.StartsWith('#') && (id.Length == 7 || id.Length == 9))
        {
            try { return (Color)ColorConverter.ConvertFromString(id); }
            catch { return null; }
        }
        return null;
    }
}
