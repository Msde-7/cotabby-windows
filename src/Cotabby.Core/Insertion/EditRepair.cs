using System.Globalization;
using System.Text;
using Cotabby.Core.Suggestions;
using Cotabby.Core.Text;

namespace Cotabby.Core.Insertion;

/// <summary>The keys that finish an edit the host only partly applied.</summary>
/// <param name="Backspaces">Backspaces to send first.</param>
/// <param name="Text">Text to type after them.</param>
public readonly record struct EditRepairPlan(int Backspaces, string Text);

/// <summary>
/// Works out what a host dropped from an edit of synthetic keys. Some hosts, Windows 11 Notepad
/// among them, now and then lose a key from input that arrives faster than a person types. Only the
/// outcomes that can be told apart for certain are repaired: some backspaces missing, the text
/// missing, or the end of the text missing. Anything else is left as it is, since a wrong repair
/// would damage the user's text.
/// <para>
/// Backspaces are counted as hosts apply them: one per code point, except that an emoji with
/// everything joined to it and a CRLF line break each go with one key. A letter and its combining
/// marks take one key per mark, as typing put them there.
/// </para>
/// </summary>
public static class EditRepair
{
    /// <param name="before">The text before the caret just before the edit.</param>
    /// <param name="deleteCount">UTF-16 units the edit deleted before the caret.</param>
    /// <param name="text">The text the edit typed.</param>
    /// <param name="live">The text before the caret after the edit.</param>
    /// <returns>The keys still needed, or null when the edit landed or its outcome is unclear.</returns>
    public static EditRepairPlan? Plan(string before, int deleteCount, string text, string live)
    {
        if (deleteCount < 0 || deleteCount > before.Length) return null;
        // Split before normalizing, since the count is of the text as it was read.
        var deleted = Keys(before[^deleteCount..]);
        string kept = Normalized(before[..^deleteCount]);
        live = Normalized(live);
        text = Normalized(text);
        if (live == kept + text) return null;

        // Every backspace landed, and the text did not, or only its start did.
        if (live.StartsWith(kept, StringComparison.Ordinal) && live.Length < kept.Length + text.Length
            && text.StartsWith(live[kept.Length..], StringComparison.Ordinal))
        {
            return new EditRepairPlan(0, text[(live.Length - kept.Length)..]);
        }

        // Some backspaces were lost. Whatever of the text landed is taken back and typed again.
        int textKeys = BackspaceCount(text);
        string partlyDeleted = kept;
        for (int lost = 1; lost <= deleted.Count; lost++)
        {
            partlyDeleted += Normalized(deleted[lost - 1]);
            if (live == partlyDeleted + text) return new EditRepairPlan(textKeys + lost, text);
            if (live == partlyDeleted) return new EditRepairPlan(lost, text);
        }
        return null;
    }

    /// <summary>Backspaces that delete <paramref name="text"/> when it ends just before the caret.</summary>
    public static int BackspaceCount(string text) => Keys(text).Count;

    /// <summary>
    /// Backspaces that delete the last <paramref name="deleteCount"/> UTF-16 units of
    /// <paramref name="preceding"/>. Units past its start count one key each.
    /// </summary>
    public static int BackspacesFor(string preceding, int deleteCount)
    {
        int known = Math.Clamp(deleteCount, 0, preceding.Length);
        return BackspaceCount(preceding[^known..]) + Math.Max(0, deleteCount - known);
    }

    /// <summary>Text before the deleted run that must not have moved either.</summary>
    private const int CheckedContext = 16;

    /// <summary>
    /// True when the text an edit was planned against still ends the field, so its backspaces
    /// delete what the plan meant them to. A key typed since would otherwise be deleted in its place.
    /// </summary>
    /// <param name="planned">The text before the caret the edit was planned against.</param>
    /// <param name="deleteCount">UTF-16 units the edit deletes before the caret.</param>
    /// <param name="live">The text before the caret now.</param>
    public static bool StillBeforeCaret(string planned, int deleteCount, string live)
    {
        if (deleteCount <= 0) return true;
        // The run and a little of the text before it: a key typed after a run like "aa" can end in it again.
        int compared = Math.Min(planned.Length, deleteCount + CheckedContext);
        return SuggestionSessionReconciler.ComparisonNormalized(live)
            .EndsWith(SuggestionSessionReconciler.ComparisonNormalized(planned[^compared..]), StringComparison.Ordinal);
    }

    /// <summary>The pieces of <paramref name="text"/> that one Backspace each removes, in order.</summary>
    private static List<string> Keys(string text)
    {
        var keys = new List<string>();
        foreach (var grapheme in new GraphemeString(text))
        {
            var cluster = grapheme.Span;
            if (cluster.Length == 1 || DeletedWhole(cluster))
            {
                keys.Add(cluster.ToString());
                continue;
            }
            for (int at = 0; at < cluster.Length;)
            {
                int length = char.IsSurrogatePair(cluster[at], at + 1 < cluster.Length ? cluster[at + 1] : '\0') ? 2 : 1;
                keys.Add(cluster.Slice(at, length).ToString());
                at += length;
            }
        }
        return keys;
    }

    /// <summary>
    /// A CRLF pair and an emoji with its skin tone, joined parts, presentation selector or keycap
    /// are deleted whole. Flags are pairs of regional indicators, which are symbols too.
    /// </summary>
    private static bool DeletedWhole(ReadOnlySpan<char> cluster)
    {
        if (cluster is "\r\n") return true;
        Rune.DecodeFromUtf16(cluster, out var first, out _);
        return Rune.GetUnicodeCategory(first) == UnicodeCategory.OtherSymbol || cluster.IndexOfAny('\uFE0F', '\u20E3') >= 0;
    }

    /// <summary>Chromium shows a typed space at the end of a line as U+00A0 for a while.</summary>
    private static string Normalized(string text) => SuggestionSessionReconciler.SpaceNormalized(text).Replace("\r", "");
}
