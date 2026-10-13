using System.Text;

namespace Cotabby.Core.Support;

/// <summary>
/// Decides whether a proposed completion would mostly retype text that already follows the caret.
/// Local models frequently restart from the text just after the cursor. Comparing on a folded view
/// (lowercased, letters and digits only) catches shapes that plain prefix matching misses, such as a
/// stray leading bullet or a case difference, while staying conservative for short completions.
/// </summary>
public static class TrailingDuplicationFilter
{
    /// <summary>
    /// Below this many folded characters an overlap is not trusted. Short coincidental matches (a
    /// shared "the") are common and must not trigger suppression.
    /// </summary>
    public const int MinimumFoldedOverlap = 3;

    /// <summary>True when <paramref name="completion"/> would duplicate text that already follows the caret.</summary>
    public static bool DuplicatesTrailingText(string completion, string trailingText)
    {
        var foldedCompletion = new GraphemeString(Fold(completion));
        if (foldedCompletion.Count < MinimumFoldedOverlap) return false;
        var foldedTrailing = new GraphemeString(Fold(trailingText));
        if (foldedTrailing.Count == 0) return false;

        // Shape 1. The completion is the start of what already follows the caret.
        if (foldedTrailing.StartsWith(foldedCompletion.Text)) return true;

        // Shape 2. The completion contains the whole upcoming suffix run, so accepting it would
        // push a second copy of that text in front of the existing one.
        if (foldedTrailing.Count >= MinimumFoldedOverlap && foldedCompletion.StartsWith(foldedTrailing.Text))
        {
            return true;
        }

        // Shape 3. A long leading run of the completion already appears at the caret, and the two
        // diverge only after a while.
        int overlap = CommonPrefixLength(foldedCompletion, foldedTrailing);
        return overlap >= Math.Max(MinimumFoldedOverlap, foldedCompletion.Count / 2);
    }

    private static string Fold(string text)
    {
        var folded = new StringBuilder(text.Length);
        foreach (var scalar in text.ToLowerInvariant().EnumerateRunes())
        {
            if (ScalarSets.IsAlphanumeric(scalar)) folded.Append(scalar.ToString());
        }
        return folded.ToString();
    }

    private static int CommonPrefixLength(GraphemeString left, GraphemeString right)
    {
        int count = 0;
        int limit = Math.Min(left.Count, right.Count);
        while (count < limit && left[count].Equals(right[count])) count++;
        return count;
    }
}
