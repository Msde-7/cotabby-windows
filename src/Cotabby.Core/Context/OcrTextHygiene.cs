using System.Text;
using Cotabby.Core.Text;

namespace Cotabby.Core.Context;

/// <summary>One recognized OCR line, with the recognizer's confidence and where it sat in the capture.</summary>
/// <param name="Bounds">Unit-square bounds with the origin at the top left, or null for text-only input.</param>
public sealed record OcrLine(string Text, float Confidence = 1f, OcrBounds? Bounds = null);

/// <summary>A rectangle in the unit square of the captured image, origin at the top left.</summary>
public readonly record struct OcrBounds(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double MidY => Y + Height / 2;
}

/// <summary>
/// Filters noisy screen-OCR lines before they become prompt context. OCR over a window recovers
/// UI chrome, icon glyphs and misread letters as well as prose, and a small model copies those
/// fragments straight back as a suggestion. Every guard is a pure function with its own threshold.
/// </summary>
public static class OcrTextHygiene
{
    /// <summary>Punctuation that prose, code, versions, paths and model names use legitimately.</summary>
    private const string CommonPunctuation = ".,!?;:'\"()[]{}-/&%$#@*+=<>`~_|\\";

    public static IReadOnlyList<OcrLine> DropLowConfidence(IEnumerable<OcrLine> lines, float threshold = 0.4f) =>
        lines.Where(l => l.Confidence >= threshold).ToList();

    /// <summary>A replacement character means the recognizer emitted a glyph it could not map.</summary>
    public static IReadOnlyList<OcrLine> DropReplacementCharacter(IEnumerable<OcrLine> lines) =>
        lines.Where(l => !l.Text.Contains('�')).ToList();

    /// <summary>Drops lines where more than <paramref name="threshold"/> of the characters are symbol noise.</summary>
    public static IReadOnlyList<OcrLine> DropHighSymbolDensity(IEnumerable<OcrLine> lines, double threshold = 0.2) =>
        lines.Where(l => !IsHighSymbolDensity(l.Text, threshold)).ToList();

    private static bool IsHighSymbolDensity(string text, double threshold)
    {
        int total = 0, symbols = 0;
        foreach (var scalar in text.EnumerateRunes())
        {
            total++;
            bool allowed = scalar.Value == ' '
                || (scalar.IsBmp && CommonPunctuation.Contains((char)scalar.Value))
                || ScalarSets.IsAlphanumeric(scalar);
            if (!allowed) symbols++;
        }
        return total > 0 && (double)symbols / total > threshold;
    }

    /// <summary>
    /// Drops lines with a digit inside a lowercase word (<c>qu81ity</c>), the usual sign of letters
    /// misread as digits. Trailing and leading digits and all-caps codes are left alone.
    /// </summary>
    public static IReadOnlyList<OcrLine> DropDigitSubstitution(IEnumerable<OcrLine> lines) =>
        lines.Where(l => !Tokens(l.Text).Any(TokenHasDigitSubstitution)).ToList();

    private static bool TokenHasDigitSubstitution(string token)
    {
        var scalars = token.EnumerateRunes().ToArray();
        for (int i = 0; i < scalars.Length; i++)
        {
            if (!Rune.IsNumber(scalars[i])) continue;
            bool lowerBefore = false;
            for (int j = 0; j < i && !lowerBefore; j++) lowerBefore = Rune.IsLower(scalars[j]);
            if (!lowerBefore) continue;
            for (int j = i + 1; j < scalars.Length; j++)
            {
                if (Rune.IsLetter(scalars[j])) return true;
            }
        }
        return false;
    }

    /// <summary>Drops lines whose non-space characters are mostly not letters or digits.</summary>
    public static IReadOnlyList<OcrLine> DropLowWordCharacterRatio(IEnumerable<OcrLine> lines, double threshold = 0.5) =>
        lines.Where(l => WordCharacterRatio(l.Text) >= threshold).ToList();

    private static double WordCharacterRatio(string text)
    {
        int nonSpace = 0, word = 0;
        foreach (var scalar in text.EnumerateRunes())
        {
            if (ScalarSets.IsWhitespaceOrNewline(scalar)) continue;
            nonSpace++;
            if (ScalarSets.IsAlphanumeric(scalar)) word++;
        }
        return nonSpace == 0 ? 0 : (double)word / nonSpace;
    }

    /// <summary>
    /// Removes lines that only echo the focused field, which the capture usually includes. Lines
    /// shorter than <paramref name="minMatch"/> stay, so a stray "to" is not matched everywhere.
    /// </summary>
    public static IReadOnlyList<OcrLine> Strip(IEnumerable<OcrLine> lines, string fieldText, int minMatch = 4) =>
        StripNormalized(lines, Normalize(fieldText), minMatch);

    private static IReadOnlyList<OcrLine> StripNormalized(IEnumerable<OcrLine> lines, string field, int minMatch)
    {
        if (field.Length == 0) return lines.ToList();
        return lines.Where(l =>
        {
            string line = Normalize(l.Text);
            return GraphemeString.CountOf(line) < minMatch || !field.Contains(line, StringComparison.Ordinal);
        }).ToList();
    }

    /// <summary>Lowercased, with whitespace runs collapsed to one space, as <see cref="Strip"/> compares text.</summary>
    internal static string Normalize(string text) =>
        string.Join(' ', text.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>Runs every guard, cheapest first, then bounds the lines and characters.</summary>
    public static string Clean(IEnumerable<OcrLine> lines, string fieldText, int maxLines = 40, int maxChars = 2000) =>
        CleanNormalized(lines, Normalize(fieldText), maxLines, maxChars);

    /// <summary>
    /// <see cref="Clean"/> against field text already passed through <see cref="Normalize"/>, so a
    /// caller cleaning line by line normalizes the field once.
    /// </summary>
    internal static string CleanNormalized(IEnumerable<OcrLine> lines, string normalizedField, int maxLines = 40, int maxChars = 2000)
    {
        IEnumerable<OcrLine> filtered = DropLowConfidence(lines);
        filtered = DropReplacementCharacter(filtered);
        filtered = DropHighSymbolDensity(filtered);
        filtered = DropDigitSubstitution(filtered);
        filtered = DropLowWordCharacterRatio(filtered);
        filtered = StripNormalized(filtered, normalizedField, minMatch: 4);

        string joined = string.Join('\n', filtered
            .Select(l => ScalarSets.TrimWhitespaceAndNewlines(l.Text))
            .Where(t => t.Length > 0)
            .Take(Math.Max(0, maxLines)));
        return new GraphemeString(joined).Prefix(Math.Max(0, maxChars));
    }

    private static IEnumerable<string> Tokens(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
}

/// <summary>
/// Chooses which cleaned OCR lines reach the prompt. Lines near the focused field, and in its
/// column, come ahead of distant window chrome, then the chosen lines return to reading order.
/// </summary>
public static class VisualContextExcerptSelector
{
    public static string Select(IReadOnlyList<OcrLine> lines, string fieldText, OcrBounds? focusBounds, int maxCharacters)
    {
        if (maxCharacters <= 0) return "";
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var candidates = new List<(int Index, string Text, double Score)>();
        string field = OcrTextHygiene.Normalize(fieldText);
        for (int index = 0; index < lines.Count; index++)
        {
            var line = lines[index];
            string text = PromptContextSanitizer.Sanitize(OcrTextHygiene.CleanNormalized([line], field, maxChars: maxCharacters));
            if (text.Length == 0 || !seen.Add(text.ToLowerInvariant())) continue;
            double score;
            if (line.Bounds is { } bounds && focusBounds is { } focus)
            {
                // A message in the editor's column outranks an equally close sidebar.
                bool sameColumn = bounds.Right >= focus.X && bounds.X <= focus.Right;
                score = Math.Abs(bounds.MidY - focus.MidY) + (sameColumn ? 0 : 1);
            }
            else
            {
                // Without geometry, favor the latest lines over the toolbar at the top.
                score = -index;
            }
            candidates.Add((index, text, score));
        }

        int remaining = maxCharacters;
        var selected = new List<(int Index, string Text)>();
        foreach (var candidate in candidates.OrderBy(c => c.Score).ThenBy(c => c.Index))
        {
            int separator = selected.Count == 0 ? 0 : 1;
            if (remaining <= separator) break;
            int length = GraphemeString.CountOf(candidate.Text);
            if (length + separator <= remaining)
            {
                selected.Add((candidate.Index, candidate.Text));
                remaining -= length + separator;
            }
            else if (selected.Count == 0)
            {
                selected.Add((candidate.Index, new GraphemeString(candidate.Text).Prefix(remaining)));
                remaining = 0;
            }
        }
        return string.Join('\n', selected.OrderBy(s => s.Index).Select(s => s.Text));
    }
}

/// <summary>How much of the screen is captured and how much recognized text may reach a prompt.</summary>
public sealed record VisualContextConfiguration(
    int SnapshotDimension,
    int MaxImageDimension,
    int MinRecognizedCharacterCount,
    int MaxRecognizedCharacters,
    int MaxSummaryCharacters,
    bool CapturesEntireWindow)
{
    /// <summary>The local model sees the whole window. The text never leaves the PC.</summary>
    public static VisualContextConfiguration Local { get; } = new(700, 2400, 12, 12000, 4000, CapturesEntireWindow: true);

    /// <summary>
    /// An endpoint gets only a crop around the field and a smaller excerpt. Choosing a network
    /// engine is not consent to send a wider screenshot's text.
    /// </summary>
    public static VisualContextConfiguration Endpoint { get; } = new(700, 1200, 12, 5000, 1500, CapturesEntireWindow: false);

    public static VisualContextConfiguration For(bool usesEndpoint) => usesEndpoint ? Endpoint : Local;
}
