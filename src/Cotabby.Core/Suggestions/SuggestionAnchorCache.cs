namespace Cotabby.Core.Suggestions;

/// <summary>
/// A short memory of recent suggestions, each tied to the text before it, so common edits re-show
/// a suggestion at once instead of generating it again. Mirrors upstream's SuggestionAnchorCache.
/// </summary>
/// <remarks>
/// Deleting a typo puts the caret back where a remembered suggestion already applied, and typing
/// the suggested characters after the suggestion went away for another reason lands inside it.
/// One rule covers both: the text before the caret must be an anchor's text plus the first
/// <c>k</c> characters of its suggestion, for any <c>k</c> short of the whole suggestion, and what
/// is left is offered. Keeping <c>k</c> short of the whole stops a suggestion typed or accepted
/// in full from offering its own tail again. Entries expire, so a suggestion cannot come back
/// after the document changed elsewhere. Safe to use from any thread.
/// </remarks>
public sealed class SuggestionAnchorCache
{
    /// <summary>Characters of preceding text kept per anchor, enough that two places rarely collide.</summary>
    public const int PrefixTailLength = 256;

    public const int Capacity = 16;

    public static readonly TimeSpan MaxEntryAge = TimeSpan.FromSeconds(180);

    private sealed record Anchor(string Identity, string PrefixTail, string FullText);

    private readonly record struct Entry(Anchor Anchor, DateTimeOffset RecordedAt);

    private readonly object _gate = new();
    private readonly List<Entry> _entries = [];
    private readonly TimeProvider _clock;

    public SuggestionAnchorCache(TimeProvider? clock = null) => _clock = clock ?? TimeProvider.System;

    /// <summary>
    /// Remembers one suggestion. A duplicate replaces the older entry, so a suggestion generated
    /// again refreshes its expiry instead of crowding the cache.
    /// </summary>
    public void Record(string identity, string precedingText, string fullText)
    {
        if (fullText.Length == 0) return;
        var anchor = new Anchor(identity, Tail(precedingText), fullText);
        lock (_gate)
        {
            _entries.RemoveAll(e => e.Anchor == anchor);
            _entries.Add(new Entry(anchor, _clock.GetUtcNow()));
            if (_entries.Count > Capacity) _entries.RemoveRange(0, _entries.Count - Capacity);
        }
    }

    /// <summary>
    /// What is left of the freshest remembered suggestion the text before the caret is on the way
    /// to, or null. The deepest match wins, so the restore resumes where the user is.
    /// </summary>
    public string? Remainder(string identity, string precedingText)
    {
        string live = Tail(precedingText);
        lock (_gate)
        {
            var cutoff = _clock.GetUtcNow() - MaxEntryAge;
            _entries.RemoveAll(e => e.RecordedAt < cutoff);

            string? best = null;
            int bestConsumed = -1;
            // Newest first, so a tie goes to the newest.
            for (int i = _entries.Count - 1; i >= 0; i--)
            {
                var anchor = _entries[i].Anchor;
                if (anchor.Identity != identity) continue;
                if (ConsumedPrefixLength(live, anchor.PrefixTail, anchor.FullText) is not { } consumed) continue;
                if (consumed <= bestConsumed) continue;
                bestConsumed = consumed;
                best = anchor.FullText[consumed..];
            }
            return best;
        }
    }

    /// <summary>Removes everything, as when focus moves to another field.</summary>
    public void Clear()
    {
        lock (_gate) _entries.Clear();
    }

    /// <summary>
    /// The <c>k</c> with <paramref name="live"/> equal to the tail of the anchor text plus the
    /// first <c>k</c> suggested characters, with <c>k</c> short of the whole suggestion, or null.
    /// </summary>
    private static int? ConsumedPrefixLength(string live, string anchorTail, string fullText)
    {
        string composed = anchorTail + fullText;
        for (int consumed = 0; consumed < fullText.Length; consumed++)
        {
            int end = anchorTail.Length + consumed;
            int start = Math.Max(0, end - PrefixTailLength);
            if (end - start != live.Length) continue;
            if (string.CompareOrdinal(composed, start, live, 0, live.Length) == 0) return consumed;
        }
        return null;
    }

    private static string Tail(string text) => text.Length <= PrefixTailLength ? text : text[^PrefixTailLength..];
}
