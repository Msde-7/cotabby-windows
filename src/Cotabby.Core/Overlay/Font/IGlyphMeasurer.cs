namespace Cotabby.Core.Overlay.Font;

/// <summary>A typeface the ghost text can be drawn in.</summary>
/// <param name="Family">Font family name, such as "Segoe UI".</param>
/// <param name="Weight">Weight from 100 to 900. 400 is regular.</param>
/// <param name="Italic">True for the italic style.</param>
public readonly record struct GhostFace(string Family, int Weight = 400, bool Italic = false);

/// <summary>Vertical metrics of a face, each as a multiple of the em size.</summary>
/// <param name="ContentHeightPerEm">Ascent plus descent, the box a web engine lays a line's glyphs in.</param>
/// <param name="LineHeightPerEm">The face's default line spacing, which native editors use.</param>
public readonly record struct FaceMetrics(double ContentHeightPerEm, double LineHeightPerEm);

/// <summary>
/// Measures text in installed fonts. The app implements it with WPF text formatting; tests use a
/// fake with known advances. Sizes and widths share one unit, which the app takes to be DIPs.
/// </summary>
public interface IGlyphMeasurer
{
    /// <summary>True when the family can be drawn.</summary>
    bool IsInstalled(string family);

    /// <summary>Width of <paramref name="text"/> in <paramref name="face"/> at em size <paramref name="size"/>.</summary>
    double Width(string text, GhostFace face, double size);

    FaceMetrics Metrics(GhostFace face);
}
