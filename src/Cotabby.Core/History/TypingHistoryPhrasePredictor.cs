using System.Text;
using Cotabby.Core.Text;

namespace Cotabby.Core.History;

/// <summary>
/// Predicts the rest of a phrase the user has typed many times before, without a model.
/// <para>
/// After "Best regards," the user writes their name nearly every time. A word-level n-gram table
/// built from history answers that instantly and in the user's exact wording. It is strict: a
/// continuation is offered only when the same three words were followed by the same next word at
/// least <see cref="MinimumCount"/> times and in at least <see cref="MinimumShare"/> of cases, and
/// the chain stops at the first word history is unsure about. Anything less falls through to the
/// model.
/// </para>
/// </summary>
public sealed class TypingHistoryPhrasePredictor
{
    public readonly record struct Limits(int MaxWords, bool AllowsNewlines);

    private const int ContextLength = 3;
    private const int MinimumCount = 3;
    private const double MinimumShare = 0.6;

    /// <summary>The two-word fallback predicts less, so it needs more evidence and a clearer winner.</summary>
    private const int BackoffMinimumCount = 5;
    private const double BackoffMinimumShare = 0.7;

    /// <summary>Marks a context word history has never seen, so the three-word lookup misses cleanly.</summary>
    private const int UnknownToken = -1;

    /// <summary>A one-word shortcut is more likely a coincidence, so it needs more evidence.</summary>
    private const int MinimumSingleWordCount = 5;
    private const string NewlineToken = "\n";

    private readonly Dictionary<string, int> _vocabulary = [];

    /// <summary>The spelling to insert for each token, as the user last wrote it.</summary>
    private readonly List<string> _displayForms = [];
    private readonly Dictionary<(int, int, int), Dictionary<int, int>> _continuations;
    private readonly Dictionary<(int, int), Dictionary<int, int>> _shortContinuations;

    public int ContextCount => _continuations.Count;

    public TypingHistoryPhrasePredictor(IEnumerable<TypingHistoryRecord> records)
    {
        var sequences = new List<int[]>();
        foreach (var record in records)
        {
            // Learn only from what the user typed, so quoted replies never become shortcuts.
            var ids = new List<int>();
            foreach (var token in Tokens(record.TypedText))
            {
                string key = token.ToLowerInvariant();
                if (_vocabulary.TryGetValue(key, out int id))
                {
                    _displayForms[id] = token;
                }
                else
                {
                    id = _displayForms.Count;
                    _vocabulary[key] = id;
                    _displayForms.Add(token);
                }
                ids.Add(id);
            }
            sequences.Add([.. ids]);
        }

        _continuations = ContinuationCounts(sequences, ContextLength, MinimumCount,
            (ids, i) => (ids[i - 3], ids[i - 2], ids[i - 1]));
        _shortContinuations = ContinuationCounts(sequences, 2, BackoffMinimumCount,
            (ids, i) => (ids[i - 2], ids[i - 1]));
    }

    /// <summary>
    /// Next-token counts for every context seen at least <paramref name="minimumCount"/> times.
    /// Two passes keep memory bounded: count contexts first, then collect next tokens only for
    /// contexts frequent enough to ever produce a shortcut.
    /// </summary>
    private static Dictionary<TKey, Dictionary<int, int>> ContinuationCounts<TKey>(
        List<int[]> sequences, int contextLength, int minimumCount, Func<int[], int, TKey> context)
        where TKey : notnull
    {
        var contextCounts = new Dictionary<TKey, int>();
        foreach (var ids in sequences)
        {
            for (int i = contextLength; i < ids.Length; i++)
            {
                var key = context(ids, i);
                contextCounts[key] = contextCounts.GetValueOrDefault(key) + 1;
            }
        }
        var continuations = new Dictionary<TKey, Dictionary<int, int>>();
        foreach (var ids in sequences)
        {
            for (int i = contextLength; i < ids.Length; i++)
            {
                var key = context(ids, i);
                if (contextCounts[key] < minimumCount) continue;
                if (!continuations.TryGetValue(key, out var next)) continuations[key] = next = [];
                next[ids[i]] = next.GetValueOrDefault(ids[i]) + 1;
            }
        }
        return continuations;
    }

    /// <summary>
    /// The exact text to insert after <paramref name="precedingText"/>, or null when history is not
    /// confident. Inside a word ("Best reg") only the untyped rest of it is returned ("ards,
    /// Senad"). Otherwise the result starts with the separating space.
    /// </summary>
    public string? Continuation(string precedingText, Limits limits)
    {
        bool endsAtBoundary = precedingText.Length == 0 || char.IsWhiteSpace(precedingText[^1]);
        var tokens = Tokens(precedingText[Math.Max(0, precedingText.Length - 400)..]);
        string? partial = null;
        if (!endsAtBoundary && tokens.Count > 0)
        {
            partial = tokens[^1];
            tokens.RemoveAt(tokens.Count - 1);
        }
        if (tokens.Count < 2 || ContextIds(tokens) is not { } ids) return null;

        var output = new StringBuilder();
        int words = 0;
        int weakestCount = int.MaxValue;
        bool isFirstStep = true;
        while (words < limits.MaxWords)
        {
            string? prefixFilter = isFirstStep ? partial?.ToLowerInvariant() : null;
            if (ConfidentNextToken(ids, prefixFilter) is not { } next) break;
            var (nextId, count) = next;
            string token = _displayForms[nextId];

            if (token == NewlineToken)
            {
                if (!limits.AllowsNewlines || (isFirstStep && partial is not null)) break;
                output.Append('\n');
            }
            else if (isFirstStep && partial is not null)
            {
                output.Append(token[partial.Length..]);
                words++;
            }
            else
            {
                // A space separates words, except after a line break or at the very start when
                // the field already ends in whitespace.
                bool noSeparator = EndsWithNewline(output) || (output.Length == 0 && endsAtBoundary);
                output.Append(noSeparator ? "" : " ").Append(token);
                words++;
            }
            weakestCount = Math.Min(weakestCount, count);
            ids.Add(nextId);
            isFirstStep = false;
            if (token.Length > 0 && token[^1] is '.' or '!' or '?') break;
        }

        string result = output.ToString();
        if (result.Trim().Length == 0) return null;
        if (words < 2 && weakestCount < MinimumSingleWordCount) return null;
        return result.EndsWith('\n') ? result[..^1] : result;
    }

    private static bool EndsWithNewline(StringBuilder text) => text.Length > 0 && text[^1] == '\n';

    /// <summary>
    /// The ids of the three words before the caret, or null when history cannot answer. The two
    /// words nearest the caret must be known. The third may be new, and then the two-word fallback
    /// answers.
    /// </summary>
    private List<int>? ContextIds(List<string> tokens)
    {
        var ids = tokens.Count >= ContextLength ? new List<int>() : [UnknownToken];
        var last = tokens.Skip(Math.Max(0, tokens.Count - ContextLength)).ToList();
        for (int offset = 0; offset < last.Count; offset++)
        {
            bool isNearCaret = offset >= Math.Min(tokens.Count, ContextLength) - 2;
            if (_vocabulary.TryGetValue(last[offset].ToLowerInvariant(), out int id)) ids.Add(id);
            else if (isNearCaret) return null;
            else ids.Add(UnknownToken);
        }
        return ids;
    }

    /// <summary>The next token history is confident about, from the three-word table or the stricter fallback.</summary>
    private (int Id, int Count)? ConfidentNextToken(List<int> ids, string? prefix)
    {
        if (_continuations.TryGetValue((ids[^3], ids[^2], ids[^1]), out var candidates))
        {
            return ConfidentCandidate(candidates, prefix, MinimumCount, MinimumShare);
        }
        return _shortContinuations.TryGetValue((ids[^2], ids[^1]), out var shortCandidates)
            ? ConfidentCandidate(shortCandidates, prefix, BackoffMinimumCount, BackoffMinimumShare)
            : null;
    }

    private (int Id, int Count)? ConfidentCandidate(
        Dictionary<int, int> candidates, string? prefix, int minimumCount, double minimumShare)
    {
        var eligible = prefix is null
            ? candidates.ToList()
            : candidates.Where(pair =>
            {
                string form = _displayForms[pair.Key].ToLowerInvariant();
                return form != NewlineToken && form.StartsWith(prefix, StringComparison.Ordinal);
            }).ToList();
        int total = eligible.Sum(pair => pair.Value);
        if (total == 0) return null;
        var best = eligible.MaxBy(pair => pair.Value);
        if (best.Value < minimumCount || (double)best.Value / total < minimumShare) return null;
        return (best.Key, best.Value);
    }

    /// <summary>
    /// Splits on spaces and tabs, keeping punctuation attached ("regards,") and turning each line
    /// break into its own token so sign-offs that span lines can be learned.
    /// </summary>
    public static List<string> Tokens(string text)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        foreach (char character in text)
        {
            if (ScalarSets.IsNewline(character))
            {
                Flush();
                if (tokens.Count == 0 || tokens[^1] != NewlineToken) tokens.Add(NewlineToken);
            }
            else if (char.IsWhiteSpace(character))
            {
                Flush();
            }
            else
            {
                current.Append(character);
            }
        }
        Flush();
        return tokens;

        void Flush()
        {
            if (current.Length == 0) return;
            tokens.Add(current.ToString());
            current.Clear();
        }
    }
}
