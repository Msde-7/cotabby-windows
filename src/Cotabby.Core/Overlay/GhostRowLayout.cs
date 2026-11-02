namespace Cotabby.Core.Overlay;

/// <summary>
/// Splits inline ghost text into the rows it is drawn on. Ported from the macOS <c>GhostTextLayout</c>.
/// </summary>
/// <remarks>
/// The first row runs from the caret to the band's right edge and later rows take the whole band,
/// as the host's own text would wrap. A host that offers one row (a single-line field, or text
/// after the caret that a second row would cover) gets the head that fits, cut at a word and before
/// any line break; accepting reveals the rest. A null result means no honest inline layout exists
/// and the card shows the suggestion instead.
/// </remarks>
public static class GhostRowLayout
{
    /// <summary>Rows beyond this are a runaway suggestion, not something to paint over the host.</summary>
    public const int MaximumRows = 6;

    /// <param name="Text">The suggestion still to show.</param>
    /// <param name="FirstRowWidth">Room from the caret to the band's right edge.</param>
    /// <param name="BandWidth">Room for each later row, or null when the host offers one row only.</param>
    /// <param name="KeycapWidth">Room the accept-key hint takes after the text, or 0 when hidden.</param>
    /// <param name="RightToLeft">Right-to-left text, which is drawn on one row ending at the caret.</param>
    public readonly record struct Input(
        string Text, double FirstRowWidth, double? BandWidth, double KeycapWidth = 0, bool RightToLeft = false);

    /// <param name="Rows">Each row's text, line breaks left out. The first is empty when no word fits at the caret.</param>
    /// <param name="IsTruncated">True when only the head of the text fit on the one row.</param>
    /// <param name="ShowsKeycap">False when the hint was dropped to make room for the text.</param>
    public sealed record Result(IReadOnlyList<string> Rows, bool IsTruncated, bool ShowsKeycap);

    public static Result? Make(Input input, Func<string, double> width)
    {
        var withKeycap = Rows(input, width, input.KeycapWidth);
        // The ghost matters more than its hint, so a hint that cuts the text short or keeps it from
        // fitting at all is dropped.
        if (input.KeycapWidth <= 0 || withKeycap is { IsTruncated: false }) return withKeycap;
        return Rows(input, width, 0) ?? withKeycap;
    }

    private static Result? Rows(Input input, Func<string, double> width, double keycap)
    {
        string text = input.Text;
        bool showsKeycap = keycap > 0;
        if (text.Length == 0) return null;
        if (input.RightToLeft)
        {
            return NewlineAt(text, 0) < text.Length ? null : new Result([text], false, showsKeycap);
        }
        if (input.BandWidth is not { } band) return SingleRow(text, input.FirstRowWidth - keycap, width, showsKeycap);

        var rows = new List<string>();
        int start = 0;
        double budget = input.FirstRowWidth;
        while (start < text.Length)
        {
            if (rows.Count == MaximumRows) return null;
            int end = Break(text, start, budget, width);
            bool atNewline = end < text.Length && text[end] is '\r' or '\n';
            if (end == start && !atNewline)
            {
                // The caret row may stay empty once, as when the host moves a word that does not
                // fit to the next line. An empty later row means the band is too narrow for a word.
                if (rows.Count > 0) return null;
                rows.Add("");
                budget = band;
                continue;
            }
            rows.Add(text[start..end]);
            start = atNewline ? SkipNewline(text, end) : end;
            budget = band;
        }
        return new Result(rows, false, showsKeycap);
    }

    private static Result? SingleRow(string text, double budget, Func<string, double> width, bool showsKeycap)
    {
        int end = Break(text, 0, budget, width);
        bool truncated = end < text.Length;
        if (truncated)
        {
            while (end > 0 && char.IsWhiteSpace(text[end - 1])) end--;
        }
        return end == 0 ? null : new Result([text[..end]], truncated, showsKeycap);
    }

    /// <summary>
    /// The furthest word break before the next line break whose text fits <paramref name="budget"/>,
    /// or <paramref name="start"/> when not even the first word fits. Spaces at a break hang past
    /// the edge, as they do in the host.
    /// </summary>
    private static int Break(string text, int start, double budget, Func<string, double> width)
    {
        int limit = NewlineAt(text, start);
        int best = start;
        for (int p = start + 1; p <= limit; p++)
        {
            bool wordBreak = p == limit || (char.IsWhiteSpace(text[p - 1]) && !char.IsWhiteSpace(text[p]));
            if (!wordBreak) continue;
            if (width(text[start..p].TrimEnd()) > budget) break;
            best = p;
        }
        return best;
    }

    private static int NewlineAt(string text, int start)
    {
        int at = text.AsSpan(start).IndexOfAny('\r', '\n');
        return at < 0 ? text.Length : start + at;
    }

    private static int SkipNewline(string text, int at) =>
        text[at] == '\r' && at + 1 < text.Length && text[at + 1] == '\n' ? at + 2 : at + 1;
}
