namespace Cotabby.Core.Support;

/// <summary>
/// Read-only index of decoded vocabulary bytes, built once per loaded model. A caret can split a
/// token, and fixing the existing tokenization of "sched" prevents the model from choosing a token
/// for "schedule". The decoder backs up over the typed suffix and asks this index which tokens can
/// reproduce the exact bytes already typed. Matching is on bytes, independent of UTF-8 character
/// boundaries, so byte-fallback tokenizers work as well.
/// </summary>
public sealed class TokenHealingVocabulary
{
    private readonly record struct Entry(int Token, byte[] Piece, bool ContainsLineBreak);

    private readonly Entry[] _entries;

    /// <param name="entries">Token ids with their decoded bytes. Empty pieces are not indexed,
    /// because they would make no progress through a prefix.</param>
    public TokenHealingVocabulary(IEnumerable<(int Token, byte[] Piece)> entries)
    {
        _entries = entries
            .Where(entry => entry.Piece.Length > 0)
            .Select(entry => new Entry(
                entry.Token, entry.Piece, entry.Piece.AsSpan().IndexOfAny((byte)'\r', (byte)'\n') >= 0))
            .ToArray();
        Array.Sort(_entries, (left, right) => left.Piece.AsSpan().SequenceCompareTo(right.Piece));
    }

    /// <summary>
    /// Tokens covering the complete remaining prefix, including exact replay. Falls back to shorter
    /// matching pieces only when no covering token exists, as with byte-fallback text.
    /// </summary>
    public List<int> MatchingTokens(ReadOnlySpan<byte> prefix, bool singleLine = false)
    {
        var matches = new List<int>();
        if (prefix.IsEmpty) return matches;

        // Tokens that finish the replay, and may also introduce new text, form one contiguous range.
        for (int index = LowerBound(prefix); index < _entries.Length; index++)
        {
            var entry = _entries[index];
            if (!entry.Piece.AsSpan().StartsWith(prefix)) break;
            if (!singleLine || !entry.ContainsLineBreak) matches.Add(entry.Token);
        }

        // A token that accounts for all known bytes is preferred. A short token such as " i" is
        // likely because it can start many words that contradict the already typed " int", and
        // letting it compete would rank incompatible paths before their likelihood is known. Line
        // break tokens are filtered before choosing this branch, so a covering token the sampler
        // forbids cannot block a valid shorter spelling.
        if (matches.Count > 0) return matches;

        // Text with no covering token progresses through shorter exact pieces. The next step
        // applies the same rule to the remaining bytes.
        for (int length = 1; length < prefix.Length; length++)
        {
            var fragment = prefix[..length];
            for (int index = LowerBound(fragment); index < _entries.Length; index++)
            {
                var entry = _entries[index];
                if (!entry.Piece.AsSpan().SequenceEqual(fragment)) break;
                if (!singleLine || !entry.ContainsLineBreak) matches.Add(entry.Token);
            }
        }
        return matches;
    }

    private int LowerBound(ReadOnlySpan<byte> value)
    {
        int low = 0;
        int high = _entries.Length;
        while (low < high)
        {
            int middle = (low + high) / 2;
            if (_entries[middle].Piece.AsSpan().SequenceCompareTo(value) < 0) low = middle + 1;
            else high = middle;
        }
        return low;
    }
}
