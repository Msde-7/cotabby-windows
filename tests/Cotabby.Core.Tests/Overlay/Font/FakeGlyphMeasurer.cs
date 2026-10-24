using Cotabby.Core.Overlay.Font;

namespace Cotabby.Core.Tests.Overlay.Font;

/// <summary>
/// Faces with known advances: each letter takes <c>Wide</c> em and each narrow glyph (i, l, j, t,
/// f, r, space and punctuation) takes <c>Narrow</c> em. A monospace face uses one advance for both.
/// </summary>
internal sealed class FakeGlyphMeasurer : IGlyphMeasurer
{
    public readonly record struct FakeFace(double Wide, double Narrow, double Content, double Line);

    public Dictionary<string, FakeFace> Faces { get; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Segoe UI"] = new(0.56, 0.25, 1.33, 1.33),
        ["Segoe UI Variable"] = new(0.52, 0.23, 1.33, 1.33),
        ["Arial"] = new(0.60, 0.27, 1.15, 1.15),
        ["Calibri"] = new(0.48, 0.22, 1.22, 1.22),
        ["Consolas"] = new(0.55, 0.55, 1.17, 1.17),
        ["Cascadia Mono"] = new(0.60, 0.60, 1.17, 1.32),
        ["Georgia"] = new(0.66, 0.31, 1.14, 1.14),
        ["Times New Roman"] = new(0.50, 0.24, 1.15, 1.15),
        ["Verdana"] = new(0.72, 0.34, 1.22, 1.22),
    };

    public bool IsInstalled(string family) => Faces.ContainsKey(family);

    public double Width(string text, GhostFace face, double size)
    {
        if (!Faces.TryGetValue(face.Family, out var f)) f = Faces["Segoe UI"];
        double ems = 0;
        foreach (char c in text) ems += IsNarrow(c) ? f.Narrow : f.Wide;
        // Bold glyphs run a little wider.
        if (face.Weight >= 600) ems *= 1.05;
        return ems * size;
    }

    public FaceMetrics Metrics(GhostFace face)
    {
        if (!Faces.TryGetValue(face.Family, out var f)) f = Faces["Segoe UI"];
        return new FaceMetrics(f.Content, f.Line);
    }

    private static bool IsNarrow(char c) => c is 'i' or 'l' or 'j' or 't' or 'f' or 'r' or ' ' || char.IsPunctuation(c);
}
