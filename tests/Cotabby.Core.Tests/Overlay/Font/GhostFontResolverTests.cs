using Cotabby.Core.Overlay.Font;
using P = Cotabby.Core.Overlay.Font.GhostFontProvenance;

namespace Cotabby.Core.Tests.Overlay.Font;

public sealed class GhostFontResolverTests
{
    private readonly FakeGlyphMeasurer _m = new();

    private GhostFontResolution Resolve(
        string? name = null, double? size = null, string? sample = null, double? width = null,
        double caretBox = 16, HostRenderer renderer = HostRenderer.Native, double multiplier = 1,
        HostZoomLadder.Kind? ladder = null, int? weight = null) =>
        GhostFontResolver.Resolve(new GhostFontInput
        {
            FontName = name,
            ReportedSize = size,
            Weight = weight,
            SampleText = sample,
            SampleWidth = width,
            CaretBoxHeight = caretBox,
            Renderer = renderer,
            SizeMultiplier = multiplier,
            ZoomLadder = ladder,
        }, _m);

    private double Width(string text, string family, double size) => _m.Width(text, new GhostFace(family), size);

    [Fact]
    public void HostFaceAndSizeAreUsedExactlyWithNoFloor()
    {
        var r = Resolve("Consolas", 11, caretBox: 13);
        Assert.Equal("Consolas", r.Face.Family);
        Assert.Equal(11, r.Size);
        Assert.Equal(P.HostFace, r.Provenance);
    }

    [Fact]
    public void HostWeightAndItalicAreKept()
    {
        var r = GhostFontResolver.Resolve(
            new GhostFontInput { FontName = "Georgia", ReportedSize = 18, Weight = 700, Italic = true, CaretBoxHeight = 21 }, _m);
        Assert.Equal(new GhostFace("Georgia", 700, true), r.Face);
    }

    [Fact]
    public void ACssStackUsesItsFirstInstalledFamily()
    {
        var r = Resolve("\"Google Sans\", Roboto, Georgia, serif", 18, caretBox: 21);
        Assert.Equal("Georgia", r.Face.Family);
        Assert.Equal(P.HostFace, r.Provenance);
    }

    [Fact]
    public void SizeOnlyWithMeasuredWidthMatchesTheMonospaceFamily()
    {
        const string sample = "alpha bravo charlie";
        var r = Resolve(size: 13, sample: sample, width: Width(sample, "Consolas", 13), caretBox: 15, renderer: HostRenderer.Web);
        Assert.Equal("Consolas", r.Face.Family);
        Assert.Equal(13, r.Size);
        Assert.Equal(P.HostSizeMatchedFamily, r.Provenance);
        Assert.Equal(1, r.WidthAgreement, 3);
    }

    [Fact]
    public void SizeOnlyWithMeasuredWidthMatchesGeorgia()
    {
        const string sample = "delta echo foxtrot golf hotel";
        var r = Resolve(size: 18, sample: sample, width: Width(sample, "Georgia", 18), caretBox: 21, renderer: HostRenderer.Web);
        Assert.Equal("Georgia", r.Face.Family);
        Assert.Equal(P.HostSizeMatchedFamily, r.Provenance);
    }

    [Fact]
    public void SizeOnlyWithoutSampleUsesSystemFaceAtHostSize()
    {
        var r = Resolve(size: 15, caretBox: 17, renderer: HostRenderer.Web);
        Assert.Equal(15, r.Size);
        Assert.Equal(GhostFontResolver.SystemFamily, r.Face.Family);
        Assert.Equal(P.HostSizeSystem, r.Provenance);
    }

    [Fact]
    public void SizeOnlyWithUnmatchedWidthScalesSystemFaceToTheMeasurement()
    {
        const string sample = "alpha bravo charlie delta";
        // A third narrower than the system face: no candidate is that condensed.
        double host = Width(sample, "Segoe UI", 14) * 0.66;
        var r = Resolve(size: 14, sample: sample, width: host, caretBox: 17, renderer: HostRenderer.Web);
        Assert.Equal(P.HostSizeScaledSystem, r.Provenance);
        Assert.Equal(1, Width(sample, r.Face.Family, r.Size) / host, 2);
    }

    [Fact]
    public void ACaretBoxFarTallerThanTheReportedSizeFallsBackToTheBox()
    {
        // A 13 size cannot own a 40 high line box; the box is the measurement to trust.
        var r = Resolve("Arial", 13, caretBox: 40);
        Assert.Equal(P.CaretDerived, r.Provenance);
        Assert.True(r.Size > 13);
    }

    [Fact]
    public void ACaretBoxShorterThanTheReportedSizeNeverShrinksTheFace()
    {
        // VS Code's hidden text area reports a caret box shorter than its editor's glyphs.
        var r = Resolve(size: 14, caretBox: 8.5);
        Assert.Equal(14, r.Size);
        Assert.NotEqual(P.CaretDerived, r.Provenance);
    }

    [Fact]
    public void ASampleFromAnotherFaceDoesNotRescaleTheCaretBoxFace()
    {
        const string sample = "it is still in the list, I will fill it in";
        var boxOnly = Resolve(caretBox: 18, renderer: HostRenderer.Web);
        double host = Width(sample, "Consolas", 14.3);
        Assert.True(host / Width(sample, boxOnly.Face.Family, boxOnly.Size) > 1.15, "a face apart, not a size");

        var r = Resolve(sample: sample, width: host, caretBox: 18, renderer: HostRenderer.Web);
        Assert.Equal(P.CaretDerived, r.Provenance);
        Assert.Equal(boxOnly.Size, r.Size, 2);
    }

    [Fact]
    public void ASampleCloseToTheCaretBoxFaceStillCalibratesIt()
    {
        const string sample = "it is still in the list, I will fill it in";
        var boxOnly = Resolve(caretBox: 18, renderer: HostRenderer.Web);
        double host = Width(sample, boxOnly.Face.Family, boxOnly.Size) * 1.06;
        var r = Resolve(sample: sample, width: host, caretBox: 18, renderer: HostRenderer.Web);
        Assert.Equal(P.CaretDerivedCalibrated, r.Provenance);
        Assert.Equal(1, Width(sample, r.Face.Family, r.Size) / host, 2);
    }

    [Fact]
    public void NoStyleDerivesSizeFromTheNativeLineHeight()
    {
        var r = Resolve(caretBox: 16);
        Assert.Equal(P.CaretDerived, r.Provenance);
        Assert.Equal(16, _m.Metrics(r.Face).LineHeightPerEm * r.Size, 0);
    }

    [Fact]
    public void NoStyleDerivesSizeFromTheWebContentArea()
    {
        var r = Resolve(name: "Arial", caretBox: 21, renderer: HostRenderer.Web);
        Assert.Equal("Arial", r.Face.Family);
        Assert.InRange(_m.Metrics(r.Face).ContentHeightPerEm * r.Size, 21 - 1.2, 21 + 1.2);
    }

    [Fact]
    public void SizeMultiplierScalesTheHostSizeOnlyWhenNotOne()
    {
        Assert.Equal(14, Resolve("Consolas", 14, multiplier: 1).Size);
        var scaled = Resolve("Consolas", 14, multiplier: 1.5);
        Assert.Equal(21, scaled.Size);
        Assert.Equal("Consolas", scaled.Face.Family);
    }

    [Fact]
    public void ANamedFaceIsScaledWhenTheHostMeasuresItWider()
    {
        const string sample = "The quick brown fox";
        double host = Width(sample, "Consolas", 12) * 1.03;
        var r = Resolve("Consolas", 12, sample, host, caretBox: 17);
        Assert.Equal(P.HostFaceScaled, r.Provenance);
        Assert.Equal(1, r.WidthAgreement, 2);
        Assert.True(r.Size > 12);
    }

    [Fact]
    public void ANamedFaceWithinRoundingOfTheSampleKeepsItsSize()
    {
        const string sample = "The quick brown fox";
        var r = Resolve("Consolas", 12, sample, Width(sample, "Consolas", 12) * 1.01, caretBox: 17);
        Assert.Equal(P.HostFace, r.Provenance);
        Assert.Equal(12, r.Size);
    }

    [Fact]
    public void AnElectronHostsMeasuredSizeSnapsToItsZoomLevel()
    {
        // Electron's level 0.5 paints 14 at 14 * 1.0954. A sample read a little short still lands there.
        const string sample = "the composer paints this line";
        double host = Width(sample, "Segoe UI", 14 * 1.0954451150103321) * 0.996;
        var r = Resolve("Segoe UI", 14, sample, host, caretBox: 22, renderer: HostRenderer.Web, ladder: HostZoomLadder.Kind.Electron);
        Assert.Equal(P.HostFaceScaled, r.Provenance);
        Assert.Equal(14 * 1.0954451150103321, r.Size, 4);
    }

    [Fact]
    public void UnavailableNamedFaceUsesTheScaledSystemFace()
    {
        const string sample = "delta echo foxtrot golf hotel";
        double host = Width(sample, "Georgia", 17);
        var r = Resolve("Google Sans Text", 17, sample, host, caretBox: 21, renderer: HostRenderer.Web);
        Assert.Equal(P.HostSizeScaledSystem, r.Provenance);
        Assert.NotEqual("Georgia", r.Face.Family);
        Assert.Equal(1, r.WidthAgreement, 2);
    }

    [Fact]
    public void UnavailableNamedFaceWithoutASampleUsesTheSystemFaceAtHostSize()
    {
        var r = Resolve("Google Sans Text", 17, caretBox: 21, renderer: HostRenderer.Web);
        Assert.Equal(P.HostSizeSystem, r.Provenance);
        Assert.Equal(17, r.Size);
    }

    [Fact]
    public void TheSystemFaceStaysWhenAFamilyFitsOnlyByChance()
    {
        // The system face is 0.4% off and another family 0.2% off: the system face stays.
        const string sample = " was thinking t";
        double system = Width(sample, "Segoe UI", 15);
        _m.Faces["Verdana"] = new(0.56 * 1.002, 0.25 * 1.002, 1.22, 1.22);
        var r = Resolve(size: 15, sample: sample, width: system * 1.004, caretBox: 18, renderer: HostRenderer.Web);
        Assert.Equal(P.HostSizeSystem, r.Provenance);
        Assert.Equal(GhostFontResolver.SystemFamily, r.Face.Family);
    }

    [Fact]
    public void FamilyFitsJudgesOneSample()
    {
        const string sample = "Thanks for sending the report";
        Assert.True(GhostFontResolver.FamilyFits("Consolas", sample, Width(sample, "Consolas", 13), 13, _m));
        Assert.False(GhostFontResolver.FamilyFits("Consolas", sample, Width(sample, "Arial", 13), 13, _m));
        Assert.False(GhostFontResolver.FamilyFits("Missing Face", sample, 100, 13, _m));
    }
}
