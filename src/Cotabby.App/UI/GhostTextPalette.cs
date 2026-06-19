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

    public static readonly IReadOnlyList<Choice> Choices =
    [
        new Choice("auto",   "Automatic (match system theme)"),
        new Choice("gray",   "Gray",         Color.FromRgb(0x73, 0x73, 0x73)),
        new Choice("blue",   "Blue",         Color.FromRgb(0x21, 0x59, 0xC8)),
        new Choice("teal",   "Teal",         Color.FromRgb(0x14, 0x9A, 0x9A)),
        new Choice("green",  "Green",        Color.FromRgb(0x2E, 0x8B, 0x57)),
        new Choice("purple", "Purple",       Color.FromRgb(0x7E, 0x4B, 0xC1)),
        new Choice("orange", "Orange",       Color.FromRgb(0xCC, 0x6E, 0x1A)),
        new Choice("red",    "Red",          Color.FromRgb(0xC8, 0x3A, 0x3A)),
    ];

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
