namespace Cotabby.Core.Overlay.Font;

/// <summary>How the host draws its lines, which decides how a caret box height maps to a font size.</summary>
public enum HostRenderer
{
    /// <summary>A native editor, whose lines are the face's own line spacing.</summary>
    Native,

    /// <summary>A web engine, whose caret box is the glyphs' content area.</summary>
    Web,
}

/// <summary>Where a resolved ghost font came from, for logs.</summary>
public enum GhostFontProvenance
{
    /// <summary>The family and size the host reported.</summary>
    HostFace,

    /// <summary>The reported family, its size scaled to the width the host painted.</summary>
    HostFaceScaled,

    /// <summary>The host gave a size and no usable family, and a candidate family matched its width.</summary>
    HostSizeMatchedFamily,

    /// <summary>The system face at the host's size.</summary>
    HostSizeSystem,

    /// <summary>The system face scaled to the width the host painted.</summary>
    HostSizeScaledSystem,

    /// <summary>A size solved from the caret box height.</summary>
    CaretDerived,

    /// <summary>A size solved from the caret box and then fitted to the host's width.</summary>
    CaretDerivedCalibrated,
}

/// <summary>What is known about the host's text. Sizes and widths are in the measurer's unit.</summary>
public sealed record GhostFontInput
{
    /// <summary>The family or CSS font stack the host names.</summary>
    public string? FontName { get; init; }

    /// <summary>The font size the host reports.</summary>
    public double? ReportedSize { get; init; }

    public int? Weight { get; init; }
    public bool? Italic { get; init; }

    /// <summary>Height of the host's caret box, the only size signal when the host reports none.</summary>
    public double CaretBoxHeight { get; init; }

    /// <summary>Text from the start of the caret's line to the caret.</summary>
    public string? SampleText { get; init; }

    /// <summary>The width the host painted <see cref="SampleText"/> at.</summary>
    public double? SampleWidth { get; init; }

    public HostRenderer Renderer { get; init; }

    /// <summary>The zoom ladder of a Chromium host, used to snap a measured size.</summary>
    public HostZoomLadder.Kind? ZoomLadder { get; init; }

    internal bool HasSample => SampleWidth is > 0 && !string.IsNullOrEmpty(SampleText);
}

/// <summary>The face and size to draw the ghost in.</summary>
/// <param name="WidthAgreement">
/// The host's sample width over this font's width of the same text. 1 when there was no sample.
/// </param>
public readonly record struct GhostFontResolution(
    GhostFace Face, double Size, GhostFontProvenance Provenance, double WidthAgreement);

/// <summary>
/// Picks the face and size the ghost text is drawn in, so its glyphs take the room the host's text
/// will once accepted. Ported from the macOS <c>GhostFontResolver</c>. Each step prefers a measured
/// fact over a guess:
/// 1. A family the host names, at the size it reports, scaled only when the host's own painted
///    width disagrees.
/// 2. A size with no usable family: the candidate face whose width matches the host's sample, else
///    the system face scaled to the sample.
/// 3. No size: the size whose line metric equals the caret box, fitted to the sample when the
///    sample is close enough to be a size and not a different face.
/// </summary>
public static class GhostFontResolver
{
    /// <summary>The face Windows draws its interface and Chromium its system-ui text in.</summary>
    public const string SystemFamily = "Segoe UI";

    /// <summary>
    /// Faces the width match tries when the host reports a size but no usable family. Ties go to
    /// the earlier entry.
    /// </summary>
    public static readonly string[] CandidateFamilies =
    [
        "Segoe UI", "Segoe UI Variable", "Arial", "Calibri", "Aptos", "Consolas", "Cascadia Mono",
        "Georgia", "Times New Roman", "Verdana",
    ];

    /// <summary>Relative width error below which a candidate face is taken as the host's.</summary>
    public const double WidthMatchTolerance = 0.02;

    /// <summary>How much better than the system face a family must fit to replace it.</summary>
    public const double FamilyMargin = 0.01;

    /// <summary>Width disagreement a named face may show before its size follows the measurement.</summary>
    public const double NamedFaceScaleTolerance = 0.015;

    public const double MinimumScale = 0.7;
    public const double MaximumScale = 1.4;
    public const double MinimumSize = 6;
    public const double MaximumSize = 128;

    /// <summary>
    /// How far a sample may rescale the face solved from a caret box. Beyond this the sample
    /// describes another face and not another size.
    /// </summary>
    public const double MaximumCaretBoxScale = 1.15;

    /// <summary>Caret box assumed when the host gives none.</summary>
    private const double DefaultCaretBoxHeight = 17;

    public static GhostFontResolution Resolve(GhostFontInput input, IGlyphMeasurer m)
    {
        string? family = FontStack.FirstInstalled(input.FontName, m);
        double? reported = PlausibleReportedSize(input, family, m);
        if (reported is { } size)
        {
            if (family is not null) return FittedToSample(Face(family, input), size, input, m);
            // The host named a face this machine does not have. Guessing an installed family from
            // a width sample is wrong by construction, so the system face stands in, scaled.
            if (FontStack.Families(input.FontName).Count > 0) return ScaledSystem(size, input, m);
            return ResolveBySize(size, input, m);
        }
        return ResolveFromCaretBox(input, family, m);
    }

    /// <summary>
    /// The reported size, or null when there is none or it cannot describe glyphs in the caret box.
    /// A box far taller than the glyphs is not this size's line. A box shorter than the glyphs is
    /// not the text's line at all (a hidden text area's caret), and the reported size stands.
    /// </summary>
    private static double? PlausibleReportedSize(GhostFontInput input, string? family, IGlyphMeasurer m)
    {
        if (input.ReportedSize is not { } size || size < MinimumSize || size > MaximumSize) return null;
        if (input.CaretBoxHeight <= 0) return size;
        double content = m.Metrics(Face(family ?? SystemFamily, input)).ContentHeightPerEm * size;
        return content * 2.2 >= input.CaretBoxHeight ? size : null;
    }

    /// <summary>
    /// A named face is trusted as is unless the host's painted text disagrees with it by more than
    /// rounding allows. Then the face is kept and its size follows the measurement, snapped to the
    /// host's zoom ladder when it has one.
    /// </summary>
    private static GhostFontResolution FittedToSample(GhostFace face, double size, GhostFontInput input, IGlyphMeasurer m)
    {
        double agreement = WidthAgreement(face, size, input, m);
        if (Math.Abs(agreement - 1) <= NamedFaceScaleTolerance || !input.HasSample)
        {
            return new(face, size, GhostFontProvenance.HostFace, agreement);
        }
        double scaled = ScaledToSample(face, size, input, m);
        if (input.ZoomLadder is { } ladder && HostZoomLadder.SnappedSize(scaled, size, ladder) is { } snapped)
        {
            scaled = snapped;
        }
        return new(face, scaled, GhostFontProvenance.HostFaceScaled, WidthAgreement(face, scaled, input, m));
    }

    private static GhostFontResolution ScaledSystem(double size, GhostFontInput input, IGlyphMeasurer m)
    {
        var system = Face(SystemFamily, input);
        if (!input.HasSample) return new(system, size, GhostFontProvenance.HostSizeSystem, 1);
        double scaled = ScaledToSample(system, size, input, m);
        return new(system, scaled, GhostFontProvenance.HostSizeScaledSystem, WidthAgreement(system, scaled, input, m));
    }

    /// <summary>Size known, face unknown: match the sample against the candidates, else scale.</summary>
    private static GhostFontResolution ResolveBySize(double size, GhostFontInput input, IGlyphMeasurer m)
    {
        var system = Face(SystemFamily, input);
        if (!input.HasSample) return new(system, size, GhostFontProvenance.HostSizeSystem, 1);

        double systemError = RelativeError(system, size, input, m);
        var best = (Face: system, Error: systemError);
        foreach (var family in CandidateFamilies)
        {
            if (family == SystemFamily || !m.IsInstalled(family)) continue;
            var candidate = Face(family, input);
            double error = RelativeError(candidate, size, input, m);
            if (error < best.Error) best = (candidate, error);
        }
        // The system face stays unless a family fits clearly better. A whole-pixel sample is off
        // by up to half a percent, so some family often fits a little better by chance.
        if (systemError <= WidthMatchTolerance && best.Error + FamilyMargin >= systemError)
        {
            best = (system, systemError);
        }
        if (best.Error <= WidthMatchTolerance)
        {
            var provenance = best.Face.Family == SystemFamily
                ? GhostFontProvenance.HostSizeSystem
                : GhostFontProvenance.HostSizeMatchedFamily;
            return new(best.Face, size, provenance, WidthAgreement(best.Face, size, input, m));
        }
        double scaled = ScaledToSample(system, size, input, m);
        return new(system, scaled, GhostFontProvenance.HostSizeScaledSystem, WidthAgreement(system, scaled, input, m));
    }

    /// <summary>No usable size: solve the size whose line metric equals the caret box.</summary>
    private static GhostFontResolution ResolveFromCaretBox(GhostFontInput input, string? family, IGlyphMeasurer m)
    {
        var face = Face(family ?? SystemFamily, input);
        double box = input.CaretBoxHeight > 0 ? input.CaretBoxHeight : DefaultCaretBoxHeight;
        var metrics = m.Metrics(face);
        double unit = input.Renderer == HostRenderer.Web ? metrics.ContentHeightPerEm : metrics.LineHeightPerEm;
        double solved = ClampedSize(Math.Round(box / Math.Max(unit, 0.01) * 2) / 2);
        if (!input.HasSample) return new(face, solved, GhostFontProvenance.CaretDerived, 1);

        double calibrated = ScaledToSample(face, solved, input, m);
        double scale = calibrated / Math.Max(solved, 0.01);
        // A sample this far from the box's face is another face, not another size. The box's size
        // stands until something names the face.
        if (scale < 1 / MaximumCaretBoxScale || scale > MaximumCaretBoxScale)
        {
            return new(face, solved, GhostFontProvenance.CaretDerived, WidthAgreement(face, solved, input, m));
        }
        return new(face, calibrated, GhostFontProvenance.CaretDerivedCalibrated, WidthAgreement(face, calibrated, input, m));
    }

    /// <summary>Whether a family reproduces one sample within the match tolerance.</summary>
    public static bool FamilyFits(string family, string sample, double width, double size, IGlyphMeasurer m)
    {
        if (!(width > 0) || string.IsNullOrEmpty(sample) || !m.IsInstalled(family)) return false;
        double measured = m.Width(sample, new GhostFace(family), size);
        return measured > 0 && Math.Abs(measured - width) / width <= WidthMatchTolerance;
    }

    /// <summary>
    /// The size at which <paramref name="face"/> paints the sample at the host's width, each step
    /// held to the scale bounds so one noisy sample cannot produce an absurd size. Iterated because
    /// hinted advances do not scale exactly with size.
    /// </summary>
    private static double ScaledToSample(GhostFace face, double size, GhostFontInput input, IGlyphMeasurer m)
    {
        double current = size;
        for (int i = 0; i < 3; i++)
        {
            double measured = m.Width(input.SampleText!, face, current);
            if (!(measured > 0)) return current;
            double scale = Math.Clamp(input.SampleWidth!.Value / measured, MinimumScale, MaximumScale);
            if (Math.Abs(scale - 1) <= 0.003) return current;
            current = ClampedSize(current * scale);
        }
        return current;
    }

    private static double RelativeError(GhostFace face, double size, GhostFontInput input, IGlyphMeasurer m)
    {
        double measured = m.Width(input.SampleText!, face, size);
        double width = input.SampleWidth!.Value;
        return measured > 0 ? Math.Abs(measured - width) / width : double.MaxValue;
    }

    private static double WidthAgreement(GhostFace face, double size, GhostFontInput input, IGlyphMeasurer m)
    {
        if (!input.HasSample) return 1;
        double measured = m.Width(input.SampleText!, face, size);
        return measured > 0 ? input.SampleWidth!.Value / measured : 1;
    }

    private static GhostFace Face(string family, GhostFontInput input) =>
        new(family, input.Weight ?? 400, input.Italic ?? false);

    private static double ClampedSize(double size) => Math.Clamp(size, MinimumSize, MaximumSize);
}
