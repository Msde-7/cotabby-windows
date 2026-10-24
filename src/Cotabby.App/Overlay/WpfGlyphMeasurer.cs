using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using Cotabby.Core.Overlay.Font;

namespace Cotabby.App.Overlay;

/// <summary>
/// Measures text with WPF's text formatting, in DIPs. Used on the overlay's UI thread only.
/// </summary>
/// <remarks>
/// Office draws its document fonts (Aptos and the like) from a per-user cloud font cache that is
/// not installed system wide. Those fonts are only looked up while <see cref="UseOfficeCloudFonts"/>
/// is set, which the overlay does for Office hosts alone.
/// </remarks>
internal sealed class WpfGlyphMeasurer : IGlyphMeasurer
{
    private static readonly string CloudFontsRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Microsoft", "FontCache", "4", "CloudFonts");

    // Listing the system fonts takes a while, so it waits for the first host font.
    private readonly Lazy<HashSet<string>> _installed = new(InstalledFamilies);
    private readonly Dictionary<string, FontFamily?> _cloudFamilies = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(GhostFace, bool), (Typeface Typeface, FaceMetrics Metrics)> _faces = [];

    /// <summary>When set, Office's cloud fonts count as installed.</summary>
    public bool UseOfficeCloudFonts { get; set; }

    public bool IsInstalled(string family) =>
        _installed.Value.Contains(family) || (UseOfficeCloudFonts && CloudFamily(family) is not null);

    /// <summary>The WPF family to draw <paramref name="family"/> with.</summary>
    public FontFamily Family(string family) =>
        !_installed.Value.Contains(family) && UseOfficeCloudFonts && CloudFamily(family) is { } cloud
            ? cloud
            : new FontFamily(family);

    public double Width(string text, GhostFace face, double size)
    {
        if (text.Length == 0 || !(size > 0)) return 0;
        var formatted = new FormattedText(
            text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face(face).Typeface, size,
            Brushes.Black, null, TextFormattingMode.Ideal, 1.0);
        return formatted.WidthIncludingTrailingWhitespace;
    }

    public FaceMetrics Metrics(GhostFace face) => Face(face).Metrics;

    private (Typeface Typeface, FaceMetrics Metrics) Face(GhostFace face)
    {
        var key = (face, UseOfficeCloudFonts);
        if (_faces.TryGetValue(key, out var cached)) return cached;
        var family = Family(face.Family);
        var typeface = new Typeface(
            family, face.Italic ? FontStyles.Italic : FontStyles.Normal,
            FontWeight.FromOpenTypeWeight(Math.Clamp(face.Weight, 1, 999)), FontStretches.Normal);
        double content = typeface.TryGetGlyphTypeface(out var glyphs) ? glyphs.Height : family.LineSpacing;
        var entry = (typeface, new FaceMetrics(content, family.LineSpacing));
        _faces[key] = entry;
        return entry;
    }

    private static HashSet<string> InstalledFamilies()
    {
        var installed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var family in Fonts.SystemFontFamilies)
        {
            foreach (var name in family.FamilyNames.Values) installed.Add(name);
            installed.Add(family.Source);
        }
        return installed;
    }

    /// <summary>A family from Office's cloud font cache, which keeps one folder per family.</summary>
    private FontFamily? CloudFamily(string family)
    {
        if (_cloudFamilies.TryGetValue(family, out var known)) return known;
        FontFamily? found = null;
        try
        {
            string folder = Path.Combine(CloudFontsRoot, family);
            if (family.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && Directory.Exists(folder))
            {
                found = Fonts.GetFontFamilies(folder + Path.DirectorySeparatorChar)
                    .FirstOrDefault(f => f.FamilyNames.Values.Any(n => n.Equals(family, StringComparison.OrdinalIgnoreCase)));
            }
        }
        catch (Exception) { /* not available */ }
        _cloudFamilies[family] = found;
        return found;
    }
}
