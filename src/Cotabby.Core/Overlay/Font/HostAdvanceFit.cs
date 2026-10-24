namespace Cotabby.Core.Overlay.Font;

/// <summary>
/// The host's text size, read from where its caret lands as the user types along a line. Ported
/// from the macOS <c>HostAdvanceFit</c>.
/// </summary>
/// <remarks>
/// Every caret on one line lies on a straight line: caretX = lineStart + scale * advance(text
/// before the caret), with the advance measured in the ghost's face. The slope is the host's size
/// over the face's. The slope is the median of the pairwise slopes (Theil-Sen), so a read that
/// catches the host a glyph early or late does not bend it. The first fit spanning enough of the
/// line is adopted, and only a fit spanning twice as much replaces it, a bounded number of times,
/// so the ghost is resized a few times per field and each time by less. One instance per field.
/// </remarks>
public sealed class HostAdvanceFit
{
    public readonly record struct Capture(string Text, double CaretX);

    /// <param name="Size">The host's size for the face the fit was taken in.</param>
    /// <param name="Span">Host advance between the leftmost and rightmost capture.</param>
    public readonly record struct Fit(double Size, double Span, int Captures);

    public const int MinimumCaptures = 5;

    /// <summary>Advance a paragraph line's fit must span before it is adopted.</summary>
    public const double MinimumSpan = 150;

    /// <summary>The span a single-line field's fit is adopted at. Its text is often short.</summary>
    public const double SingleLineMinimumSpan = 60;

    /// <summary>Two captures closer than this give no slope worth taking.</summary>
    public const double MinimumPairAdvance = 24;

    public const int MaximumCaptures = 40;
    public const double RefinementFactor = 2;
    public const int MaximumRefinements = 2;

    /// <summary>A fit further than this from the face's own size is not this face's line and is refused.</summary>
    public const double MaximumDeviation = 0.06;

    private readonly List<Capture> _captures = [];
    private (GhostFace Face, double Size)? _face;
    private string? _lineKey;

    public IReadOnlyList<Capture> Captures => _captures;
    public Fit? Adopted { get; private set; }
    public int Refinements { get; private set; }

    /// <summary>
    /// Records a caret capture on the line named by <paramref name="lineKey"/> and returns the fit
    /// when this capture adopted or refined it. A new face starts the field over; a new line starts
    /// its captures over and keeps what was adopted.
    /// </summary>
    public Fit? Record(
        string text, double caretX, string lineKey, GhostFace face, double size, IGlyphMeasurer measurer,
        double minimumSpan = MinimumSpan)
    {
        if (_face != (face, size))
        {
            _face = (face, size);
            Adopted = null;
            Refinements = 0;
            _captures.Clear();
        }
        if (lineKey != _lineKey)
        {
            _lineKey = lineKey;
            _captures.Clear();
        }
        _captures.Add(new Capture(text, caretX));
        if (_captures.Count > MaximumCaptures) _captures.RemoveAt(0);

        if (FitCaptures(_captures, face, size, measurer, minimumSpan) is not { } fit
            || Math.Abs(fit.Size / size - 1) > MaximumDeviation)
        {
            return null;
        }
        if (Adopted is { } adopted)
        {
            if (Refinements >= MaximumRefinements || fit.Span < adopted.Span * RefinementFactor) return null;
            Refinements++;
        }
        Adopted = fit;
        return fit;
    }

    /// <summary>
    /// The host's size from captures of one line, advances measured in the face at
    /// <paramref name="size"/>. Null with too few captures or too little span.
    /// </summary>
    public static Fit? FitCaptures(
        IReadOnlyList<Capture> captures, GhostFace face, double size, IGlyphMeasurer measurer,
        double minimumSpan = MinimumSpan)
    {
        if (captures.Count < MinimumCaptures || !(size > 0)) return null;
        var advances = captures.Select(c => measurer.Width(c.Text, face, size)).ToArray();
        var slopes = new List<double>();
        for (int first = 0; first < captures.Count; first++)
        {
            for (int second = first + 1; second < captures.Count; second++)
            {
                double run = advances[second] - advances[first];
                if (Math.Abs(run) < MinimumPairAdvance) continue;
                slopes.Add((captures[second].CaretX - captures[first].CaretX) / run);
            }
        }
        if (slopes.Count < MinimumCaptures) return null;
        slopes.Sort();
        double slope = slopes[slopes.Count / 2];
        double span = (advances.Max() - advances.Min()) * slope;
        if (!(slope > 0) || span < minimumSpan) return null;
        return new Fit(size * slope, span, captures.Count);
    }
}
