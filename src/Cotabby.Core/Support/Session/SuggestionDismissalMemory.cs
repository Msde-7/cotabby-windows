using Cotabby.Core.Text;

namespace Cotabby.Core.Support;

/// <summary>
/// Bounded, ephemeral memory of explicit dismissals. Generation and cache restoration both consult
/// it so Escape stays meaningful through focus polling and small type-through edits. It never learns
/// from mere non-acceptance and never persists user text. Times are seconds on a monotonic clock.
/// </summary>
public sealed class SuggestionDismissalMemory
{
    private const int PrecedingWindow = 256;
    private const int TrailingWindow = 192;
    private const int MaximumTypedPastAnchor = 64;
    private const int FragmentLimit = 128;
    private const int Capacity = 8;
    private const double Lifetime = 15;

    private readonly record struct Entry(
        ulong IdentityKey, string Preceding, string Trailing, string Target, double ExpiresAt);

    private readonly List<Entry> _entries = [];

    public void Record(ulong identityKey, string precedingText, string trailingText, string completion, double time)
    {
        if (completion.Length == 0) return;
        string preceding = new GraphemeString(precedingText).Suffix(PrecedingWindow);
        _entries.RemoveAll(entry => entry.ExpiresAt <= time);
        _entries.Add(new Entry(
            identityKey, preceding, new GraphemeString(trailingText).Prefix(TrailingWindow),
            preceding + FirstFragment(completion), time + Lifetime));
        if (_entries.Count > Capacity) _entries.RemoveRange(0, _entries.Count - Capacity);
    }

    public bool Suppresses(ulong identityKey, string precedingText, string trailingText, string completion, double time)
    {
        string trailing = new GraphemeString(trailingText).Prefix(TrailingWindow);
        foreach (var entry in _entries)
        {
            if (entry.ExpiresAt <= time || entry.IdentityKey != identityKey || entry.Trailing != trailing) continue;
            // The original bounded anchor is located even if a few more characters have been typed.
            if (entry.Preceding.Length == 0) continue;
            int anchor = precedingText.LastIndexOf(entry.Preceding, StringComparison.Ordinal);
            if (anchor < 0) continue;
            string typedPastAnchor = precedingText[(anchor + entry.Preceding.Length)..];
            if (GraphemeString.CountOf(typedPastAnchor) > MaximumTypedPastAnchor) continue;
            string live = precedingText[anchor..];
            var target = new GraphemeString(entry.Target);
            if (!target.StartsWith(live)) continue;
            string proposed = live + FirstFragment(completion);
            if (new GraphemeString(proposed).StartsWith(entry.Target) || target.StartsWith(proposed)) return true;
        }
        return false;
    }

    /// <summary>The leading whitespace and first word of a completion, bounded.</summary>
    private static string FirstFragment(string text)
    {
        var graphemes = new GraphemeString(text);
        int leading = graphemes.PrefixLength(g => g.IsWhitespace);
        int word = graphemes.PrefixLength(g => !g.IsWhitespace, leading);
        return graphemes.Prefix(Math.Min(leading + word, FragmentLimit));
    }
}
