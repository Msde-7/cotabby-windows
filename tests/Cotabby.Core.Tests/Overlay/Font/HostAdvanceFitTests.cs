using Cotabby.Core.Overlay.Font;

namespace Cotabby.Core.Tests.Overlay.Font;

/// <summary>
/// Carets generated the way a host lays out text: the line's start plus the face's advance at the
/// host's size, small noise on every read, and some reads that catch the host a glyph late.
/// </summary>
public sealed class HostAdvanceFitTests
{
    private const string Sentence = "There are still a lot of problems, a LOT of problems with positioning, size, etc. Keep on going";
    private const double LineStart = 339.3;
    private static readonly GhostFace Arial = new("Arial");
    private readonly FakeGlyphMeasurer _m = new();

    private List<HostAdvanceFit.Capture> Captures(
        double hostSize, int from = 20, int? to = null, int outlierEvery = 5, double outlier = 3)
    {
        var result = new List<HostAdvanceFit.Capture>();
        for (int count = from; count <= (to ?? Sentence.Length); count++)
        {
            if (!char.IsLetter(Sentence[count - 1])) continue;
            string text = Sentence[..count];
            double noise = ((count * 37) % 5 - 2) * 0.1;
            double late = result.Count % outlierEvery == outlierEvery - 1 ? outlier : 0;
            result.Add(new(text, LineStart + _m.Width(text, Arial, hostSize) + noise + late));
        }
        return result;
    }

    [Fact]
    public void TheCaretsSlopeIsTheHostsSize()
    {
        var fit = HostAdvanceFit.FitCaptures(Captures(15.336), Arial, 15.1585, _m)!.Value;
        Assert.InRange(fit.Size, 15.316, 15.356);
        Assert.True(fit.Span > 400);
    }

    [Fact]
    public void ReadsOffByAGlyphDoNotBendTheFit()
    {
        var fit = HostAdvanceFit.FitCaptures(Captures(15.336, outlierEvery: 4, outlier: 6), Arial, 15.336, _m)!.Value;
        Assert.InRange(fit.Size, 15.306, 15.366);
    }

    [Fact]
    public void TooShortALineGivesNoFit() =>
        Assert.Null(HostAdvanceFit.FitCaptures(Captures(15.336, from: 5, to: 24), Arial, 15.336, _m));

    [Fact]
    public void AdoptionHoldsUntilTheSpanDoubles()
    {
        var fit = new HostAdvanceFit();
        var adoptions = new List<HostAdvanceFit.Fit>();
        foreach (var c in Captures(15.336, from: 4))
        {
            if (fit.Record(c.Text, c.CaretX, "line", Arial, 15.1585, _m) is { } adopted) adoptions.Add(adopted);
        }
        Assert.NotEmpty(adoptions);
        Assert.True(adoptions.Count <= 1 + HostAdvanceFit.MaximumRefinements);
        for (int i = 1; i < adoptions.Count; i++)
        {
            Assert.True(adoptions[i].Span >= adoptions[i - 1].Span * HostAdvanceFit.RefinementFactor);
        }
        Assert.InRange(fit.Adopted!.Value.Size, 15.306, 15.366);
    }

    [Fact]
    public void ANewLineKeepsTheFitAndANewFaceDropsIt()
    {
        var fit = new HostAdvanceFit();
        foreach (var c in Captures(15.336)) fit.Record(c.Text, c.CaretX, "first", Arial, 15.1585, _m);
        var adopted = fit.Adopted;
        Assert.NotNull(adopted);

        fit.Record("Second", 400, "second", Arial, 15.1585, _m);
        Assert.Equal(adopted, fit.Adopted);
        Assert.Single(fit.Captures);

        fit.Record("Second", 400, "second", new GhostFace("Georgia"), 15.1585, _m);
        Assert.Null(fit.Adopted);
    }

    [Fact]
    public void AFitFarFromTheFacesSizeIsRefused()
    {
        var fit = new HostAdvanceFit();
        foreach (var c in Captures(15.336)) fit.Record(c.Text, c.CaretX, "line", Arial, 12, _m);
        Assert.Null(fit.Adopted);
    }

    [Fact]
    public void ASingleLineFieldFitsFromAShorterSpan()
    {
        var face = new GhostFace("Segoe UI");
        const string query = "nothing near 3 findings";
        var captures = new List<HostAdvanceFit.Capture>();
        for (int count = 8; count <= query.Length; count += 2)
        {
            string text = query[..count];
            captures.Add(new(text, 279 + _m.Width(text, face, 14.51)));
        }
        Assert.Null(HostAdvanceFit.FitCaptures(captures, face, 15.5, _m));
        var fit = HostAdvanceFit.FitCaptures(captures, face, 15.5, _m, HostAdvanceFit.SingleLineMinimumSpan)!.Value;
        Assert.InRange(fit.Size, 14.36, 14.66);
    }
}
