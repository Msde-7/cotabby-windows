using Cotabby.Core.Text;

namespace Cotabby.Core.History;

/// <summary>What a retrieval looks for: the stable part of the text being written plus where it is written.</summary>
/// <param name="Text">Words that characterize the current writing.</param>
/// <param name="CurrentFieldText">
/// The live field text. A history entry that contains its tail is an older snapshot of this very
/// field, and showing the model its own draft would only make it echo.
/// </param>
public sealed record TypingHistoryQuery(string Text, string BundleIdentifier, string? Domain, string CurrentFieldText)
{
    /// <summary>
    /// The query text from the caret prefix, rounded down to a whole block of words. The local
    /// runtime reuses its cache for the unchanged head of the prompt, and examples sit in that
    /// head, so they only change once a block of words has been typed.
    /// </summary>
    public static string StableText(string precedingText, int wordsPerBlock = 8, int maxWords = 48)
    {
        var words = precedingText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        int completeBlocks = words.Length / wordsPerBlock * wordsPerBlock;
        if (completeBlocks == 0) return "";
        int start = Math.Max(0, completeBlocks - maxWords);
        return string.Join(' ', words[start..completeBlocks]);
    }
}

/// <summary>
/// An immutable, searchable view of typing history for prompt examples. Built off the UI thread
/// whenever history changes and queried while a request is assembled, so lookups stay cheap. It is
/// a plain inverted index with IDF weighting and a boost for the same app or site.
/// </summary>
public sealed class TypingHistoryIndex
{
    private sealed record Document(string BundleIdentifier, string? Domain, string Text);

    /// <summary>A ranked match: the passage to show and the whole past text it came from.</summary>
    public sealed record Candidate(string DocumentText, string Passage);

    /// <summary>Documents shorter than this rarely carry enough wording to be a useful example.</summary>
    private const int MinimumDocumentCharacters = 40;

    /// <summary>Below this shared weight, matches are coincidental and only add noise.</summary>
    private const double MinimumScore = 6.0;
    private const double SameAppBoost = 1.3;
    private const double SameDomainBoost = 1.6;

    private readonly List<Document> _documents = [];
    private readonly Dictionary<string, List<int>> _postings = [];
    private readonly Dictionary<string, double> _inverseDocumentFrequency = [];

    public int DocumentCount => _documents.Count;

    public TypingHistoryIndex(IEnumerable<TypingHistoryRecord> records)
    {
        // Only what the user typed becomes an example. The rest of a field is often someone else's.
        foreach (var record in records)
        {
            string text = record.TypedText;
            if (text.Length < MinimumDocumentCharacters) continue;
            int index = _documents.Count;
            _documents.Add(new Document(record.BundleIdentifier, record.Domain, text));
            foreach (var term in Terms(text).Distinct())
            {
                if (!_postings.TryGetValue(term, out var list)) _postings[term] = list = [];
                list.Add(index);
            }
        }
        // Smoothed IDF: every shared word counts at least 1, rarer words count more.
        double count = _documents.Count;
        foreach (var (term, list) in _postings)
        {
            _inverseDocumentFrequency[term] = Math.Log((count + 1) / list.Count) + 1;
        }
    }

    /// <summary>Up to <paramref name="limit"/> passages that best match the query, best first.</summary>
    public IReadOnlyList<string> Examples(TypingHistoryQuery query, int limit = 2, int maxCharacters = 320) =>
        Examples(Candidates(query, int.MaxValue, maxCharacters), query.CurrentFieldText, limit);

    /// <summary>
    /// The best passages among <paramref name="candidates"/> that are not another version of the
    /// field's own text, without repeats. Cheap, so it runs on every request while the ranking is
    /// reused for a whole block of words.
    /// </summary>
    public static IReadOnlyList<string> Examples(
        IReadOnlyList<Candidate> candidates, string currentFieldText, int limit = 2)
    {
        string currentTail = Tail(currentFieldText);
        var passages = new List<string>();
        foreach (var candidate in candidates)
        {
            if (passages.Count >= limit) break;
            if (IsSameDocument(candidate.DocumentText, currentFieldText, currentTail)) continue;
            if (passages.Contains(candidate.Passage)) continue;
            passages.Add(candidate.Passage);
        }
        return passages;
    }

    /// <summary>
    /// Ranks past writing against the query and returns the best matches with their passages.
    /// Earlier versions of the field's own text are skipped here so they never take a slot. Whether
    /// the field's latest words appear in a match changes with every keystroke, so that check is
    /// left to <see cref="Examples(IReadOnlyList{Candidate}, string, int)"/>.
    /// </summary>
    public IReadOnlyList<Candidate> Candidates(TypingHistoryQuery query, int maxCandidates = 6, int maxCharacters = 320)
    {
        var queryTerms = Terms(query.Text).ToHashSet();
        if (queryTerms.Count == 0 || maxCandidates <= 0) return [];

        var scores = new Dictionary<int, double>();
        foreach (var term in queryTerms)
        {
            if (!_inverseDocumentFrequency.TryGetValue(term, out double weight)) continue;
            foreach (int id in _postings[term])
            {
                scores[id] = scores.GetValueOrDefault(id) + weight;
            }
        }

        var ranked = scores
            .Select(pair =>
            {
                var document = _documents[pair.Key];
                double boosted = pair.Value;
                if (document.BundleIdentifier == query.BundleIdentifier) boosted *= SameAppBoost;
                if (query.Domain is { } domain && document.Domain == domain) boosted *= SameDomainBoost;
                return (Id: pair.Key, Score: boosted);
            })
            .Where(entry => entry.Score >= MinimumScore)
            .OrderByDescending(entry => entry.Score);

        var candidates = new List<Candidate>();
        foreach (var (id, _) in ranked)
        {
            string text = _documents[id].Text;
            if (IsEarlierVersion(text, query.CurrentFieldText)) continue;
            string passage = BestPassage(text, queryTerms, maxCharacters);
            if (passage.Length == 0) continue;
            candidates.Add(new Candidate(text, passage));
            if (candidates.Count == maxCandidates) break;
        }
        return candidates;
    }

    /// <summary>The field's latest words, for spotting an entry that already contains them.</summary>
    private static string Tail(string currentText) =>
        currentText[Math.Max(0, currentText.Length - 60)..].Trim();

    /// <summary>True when an entry is an earlier or later snapshot of the text being typed.</summary>
    private static bool IsSameDocument(string text, string currentText, string currentTail) =>
        IsEarlierVersion(text, currentText)
        || (currentTail.Length >= 20 && text.Contains(currentTail, StringComparison.Ordinal));

    /// <summary>The field already contains this entry's opening.</summary>
    private static bool IsEarlierVersion(string text, string currentText)
    {
        string opening = text[..Math.Min(60, text.Length)];
        return opening.Length >= 20 && currentText.Contains(opening, StringComparison.Ordinal);
    }

    private static readonly HashSet<string> StopWords = new(
        """
        a an the and or but if then so to of in on at for with from by as is are was were be been being it its
        this that these those i you he she we they me my your our their them us do does did not no yes can could
        will would should have has had just also very more most some any all what which who when where why how
        there here than too up out about into over after before again only own same other such each few both
        one get got make made like well back still even way go going know think see want need please thanks
        thank hi hello ok okay im dont ive ill let lets sure yeah
        """.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>Lowercased words of two or more letters or digits, minus stop words.</summary>
    public static List<string> Terms(string text)
    {
        var terms = new List<string>();
        int start = -1;
        string lower = text.ToLowerInvariant();
        for (int i = 0; i <= lower.Length; i++)
        {
            bool inWord = i < lower.Length && char.IsLetterOrDigit(lower[i]);
            if (inWord)
            {
                if (start < 0) start = i;
                continue;
            }
            if (start >= 0 && i - start >= 2)
            {
                string word = lower[start..i];
                if (!StopWords.Contains(word)) terms.Add(word);
            }
            start = -1;
        }
        return terms;
    }

    /// <summary>
    /// The sentence with the most query terms, grown with its neighbors while it fits, so the
    /// example reads as the user's own sentence rather than a cut.
    /// </summary>
    private static string BestPassage(string text, HashSet<string> terms, int maxCharacters)
    {
        var sentences = SplitSentences(text);
        if (sentences.Count == 0) return "";
        var hits = sentences.Select(sentence => Terms(sentence).Count(terms.Contains)).ToList();
        int best = 0;
        for (int i = 1; i < hits.Count; i++)
        {
            if (hits[i] > hits[best]) best = i;
        }

        int lower = best;
        int upper = best;
        int length = sentences[best].Length;
        while (true)
        {
            bool canGrowDown = lower > 0 && length + sentences[lower - 1].Length + 1 <= maxCharacters;
            bool canGrowUp = upper < sentences.Count - 1 && length + sentences[upper + 1].Length + 1 <= maxCharacters;
            if (canGrowUp && (!canGrowDown || hits[upper + 1] >= hits[lower - 1]))
            {
                upper++;
                length += sentences[upper].Length + 1;
            }
            else if (canGrowDown)
            {
                lower--;
                length += sentences[lower].Length + 1;
            }
            else
            {
                break;
            }
        }
        string passage = string.Join(' ', sentences.GetRange(lower, upper - lower + 1));
        if (passage.Length <= maxCharacters) return passage;
        // One very long sentence: keep whole words up to the cap.
        string clipped = passage[..maxCharacters];
        int lastSpace = clipped.LastIndexOf(' ');
        return lastSpace >= 0 ? clipped[..lastSpace] : clipped;
    }

    private static List<string> SplitSentences(string text)
    {
        var sentences = new List<string>();
        var current = new System.Text.StringBuilder();
        foreach (char character in text)
        {
            if (ScalarSets.IsNewline(character))
            {
                AppendTrimmed(current, sentences);
                continue;
            }
            current.Append(character);
            if (character is '.' or '!' or '?') AppendTrimmed(current, sentences);
        }
        AppendTrimmed(current, sentences);
        return sentences;
    }

    private static void AppendTrimmed(System.Text.StringBuilder sentence, List<string> sentences)
    {
        string trimmed = sentence.ToString().Trim(' ', '\t');
        if (trimmed.Length > 0) sentences.Add(trimmed);
        sentence.Clear();
    }
}
