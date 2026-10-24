using Cotabby.Core.Text;

namespace Cotabby.Core.Spelling;

/// <summary>
/// An exact-prefix vocabulary, built beside each SymSpell dictionary. Every word it returns starts
/// with the typed letters. The words are sorted so a lookup is a binary search instead of a scan.
/// </summary>
public sealed class WordPrefixIndex
{
    /// <summary>More matches than this make a prefix too broad to rank, so the lookup abstains.</summary>
    private const int MaxMatches = 512;

    public readonly record struct Candidate(string Word, long Frequency);

    private readonly Candidate[] _words;

    /// <param name="contents">Dictionary lines of a word and its count.</param>
    public WordPrefixIndex(string contents)
    {
        var words = new List<Candidate>();
        foreach (var line in contents.Split('\n'))
        {
            var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 2 || !long.TryParse(fields[1], out long count) || count <= 0) continue;
            if (!new GraphemeString(fields[0]).All(g => g.IsLetter)) continue;
            words.Add(new Candidate(fields[0], count));
        }
        words.Sort((a, b) => string.CompareOrdinal(a.Word, b.Word));
        _words = [.. words];
    }

    /// <summary>The two most frequent longer words starting with <paramref name="prefix"/>, ignoring its case.</summary>
    public IReadOnlyList<Candidate> Candidates(string prefix)
    {
        prefix = prefix.ToLowerInvariant();
        if (GraphemeString.CountOf(prefix) < 3) return [];

        int low = 0;
        int high = _words.Length;
        while (low < high)
        {
            int middle = (low + high) / 2;
            if (string.CompareOrdinal(_words[middle].Word, prefix) < 0) low = middle + 1;
            else high = middle;
        }

        var matches = new List<Candidate>();
        for (; low < _words.Length && _words[low].Word.StartsWith(prefix, StringComparison.Ordinal); low++)
        {
            // Ranking a truncated subset could drop the candidate that should have won.
            if (matches.Count >= MaxMatches) return [];
            if (_words[low].Word != prefix) matches.Add(_words[low]);
        }
        return matches
            .OrderByDescending(c => c.Frequency)
            .ThenBy(c => c.Word, StringComparer.Ordinal)
            .Take(2)
            .ToList();
    }
}

/// <summary>
/// The local word ending offered when the model's result is unusable mid-word. Nothing leaves
/// the PC and no generation runs. It only ever appends letters to what was typed.
/// </summary>
public static class WordCompletionFallback
{
    /// <summary>How much more frequent the top dictionary word must be than the next one.</summary>
    private const double DictionaryMargin = 4;

    /// <summary>
    /// The letters that finish <paramref name="prefix"/>, or null when the choice is not clear.
    /// A word that appears in the document or glossary with one spelling wins. Otherwise the top
    /// dictionary word needs a clear frequency margin over the next.
    /// </summary>
    public static string? Suffix(
        string prefix, IReadOnlySet<string> references, IReadOnlyList<WordPrefixIndex.Candidate> dictionaryCandidates)
    {
        var prefixGraphemes = new GraphemeString(prefix);
        if (prefixGraphemes.Count < 3 || !prefixGraphemes.All(g => g.IsLetter)) return null;

        string lowered = prefix.ToLowerInvariant();
        var matches = references
            .Where(word => GraphemeString.CountOf(word) > prefixGraphemes.Count
                && word.ToLowerInvariant().StartsWith(lowered, StringComparison.Ordinal))
            .ToList();
        string word;
        if (matches.Select(m => m.ToLowerInvariant()).Distinct().Count() == 1)
        {
            word = matches.OrderBy(m => m, StringComparer.Ordinal).First();
        }
        else if (matches.Count > 0)
        {
            return null;
        }
        else
        {
            if (dictionaryCandidates.Count == 0) return null;
            var first = dictionaryCandidates[0];
            if (dictionaryCandidates.Count > 1 && first.Frequency < dictionaryCandidates[1].Frequency * DictionaryMargin)
            {
                return null;
            }
            word = first.Word;
        }

        var wordGraphemes = new GraphemeString(word);
        if (wordGraphemes.Count <= prefixGraphemes.Count
            || wordGraphemes.Prefix(prefixGraphemes.Count).ToLowerInvariant() != lowered)
        {
            return null;
        }
        string suffix = word[wordGraphemes.Offset(prefixGraphemes.Count)..];
        return prefixGraphemes.All(g => g.IsUppercase) ? suffix.ToUpperInvariant() : suffix;
    }
}
