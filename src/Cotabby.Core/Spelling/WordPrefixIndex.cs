namespace Cotabby.Core.Spelling;

/// <summary>
/// Immutable exact-prefix vocabulary built alongside each SymSpell dictionary. Edit distance has
/// no role here because every returned word starts with the typed letters. A sorted array allows
/// bounded binary-search lookups instead of a scan over the whole dictionary.
/// </summary>
public sealed class WordPrefixIndex
{
    private const int MinimumPrefixLength = 3;
    private const int MaximumMatches = 512;

    private readonly Candidate[] _words;

    public readonly record struct Candidate(string Word, long Frequency);

    public WordPrefixIndex(string contents)
    {
        _words = FrequencyDictionaryFormat.Entries(contents, anyWhitespaceSeparates: true)
            .Where(entry => entry.Count > 0 && Graphemes.Split(entry.Word).All(Graphemes.IsLetter))
            .Select(entry => new Candidate(entry.Word, entry.Count))
            .OrderBy(candidate => candidate.Word, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// The two most frequent words that extend <paramref name="prefix"/>, most frequent first.
    /// Empty when the prefix is shorter than three characters or matches too many words to rank.
    /// </summary>
    public IReadOnlyList<Candidate> Candidates(string prefix)
    {
        prefix = Graphemes.Canonical(prefix).ToLowerInvariant();
        if (Graphemes.Count(prefix) < MinimumPrefixLength) return [];

        int low = 0;
        int high = _words.Length;
        while (low < high)
        {
            int middle = (low + high) / 2;
            if (string.CompareOrdinal(_words[middle].Word, prefix) < 0) low = middle + 1;
            else high = middle;
        }

        var matches = new List<Candidate>();
        for (; low < _words.Length && Graphemes.HasPrefix(_words[low].Word, prefix); low++)
        {
            // A very broad prefix is ambiguous. Abstain instead of ranking a truncated subset
            // whose unseen candidates could have won.
            if (matches.Count >= MaximumMatches) return [];
            if (_words[low].Word != prefix) matches.Add(_words[low]);
        }
        return matches
            .OrderByDescending(candidate => candidate.Frequency)
            .ThenBy(candidate => candidate.Word, StringComparer.Ordinal)
            .Take(2)
            .ToArray();
    }
}

/// <summary>
/// Pure local fallback and reference vocabulary for one request. The caller supplies bounded
/// document text and the user's glossary. Reference words also keep spelling checks from
/// rejecting established jargon.
/// </summary>
public static class WordCompletionFallback
{
    // Upstream measures these three windows in grapheme clusters. UTF-16 units are used here,
    // which only makes the windows slightly smaller for text outside the basic plane.
    private const int PrecedingWindow = 4000;
    private const int TrailingWindow = 2000;
    private const int GlossaryWindow = 1300;

    private const int MinimumPrefixLength = 3;
    private const int DictionaryFrequencyMargin = 4;

    public static IReadOnlySet<string> ReferenceWords(string precedingText, string trailingText, string glossary)
    {
        var preceding = precedingText[Math.Max(0, precedingText.Length - PrecedingWindow)..];
        if (CaretWordContext.UnfinishedWord(preceding) is { } partial)
        {
            preceding = preceding[..^partial.Length];
        }
        var source = string.Join(' ', preceding, Head(trailingText, TrailingWindow), Head(glossary, GlossaryWindow));

        var words = new HashSet<string>(StringComparer.Ordinal);
        var word = new List<string>();
        // The trailing space flushes the last word.
        foreach (var character in Graphemes.Split(source + " "))
        {
            if (Graphemes.IsLetter(character) || CaretWordContext.IsConnector(character))
            {
                word.Add(character);
                continue;
            }
            if (word.Count > 0 && Graphemes.IsLetter(word[0]) && Graphemes.IsLetter(word[^1]))
            {
                words.Add(string.Concat(word));
            }
            word.Clear();
        }
        return words;
    }

    /// <summary>
    /// The letters that finish <paramref name="prefix"/>, or null when no single word clearly wins.
    /// A unique document or glossary match takes precedence. Dictionary candidates need a clear
    /// frequency margin, and frequency never changes the letters already typed.
    /// </summary>
    public static string? Suffix(
        string prefix, IReadOnlySet<string> references, IReadOnlyList<WordPrefixIndex.Candidate> dictionaryCandidates)
    {
        var prefixCharacters = Graphemes.Split(prefix);
        if (prefixCharacters.Length < MinimumPrefixLength || !prefixCharacters.All(Graphemes.IsLetter)) return null;

        var loweredPrefix = prefix.ToLowerInvariant();
        var matches = references
            .Where(reference => Graphemes.Count(reference) > prefixCharacters.Length
                && Graphemes.HasPrefix(reference.ToLowerInvariant(), loweredPrefix))
            .ToArray();

        string word;
        if (matches.Select(match => match.ToLowerInvariant()).Distinct().Count() == 1)
        {
            word = matches.Order(StringComparer.Ordinal).First();
        }
        else if (matches.Length > 0)
        {
            return null;
        }
        else if (dictionaryCandidates.Count == 1 || (dictionaryCandidates.Count > 1
            && (double)dictionaryCandidates[0].Frequency >= (double)dictionaryCandidates[1].Frequency * DictionaryFrequencyMargin))
        {
            word = dictionaryCandidates[0].Word;
        }
        else
        {
            return null;
        }

        var wordCharacters = Graphemes.Split(word);
        if (wordCharacters.Length <= prefixCharacters.Length
            || string.Concat(wordCharacters[..prefixCharacters.Length]).ToLowerInvariant() != loweredPrefix)
        {
            return null;
        }
        var suffix = string.Concat(wordCharacters[prefixCharacters.Length..]);
        return prefixCharacters.All(Graphemes.IsUppercase) ? Graphemes.Uppercased(suffix) : suffix;
    }

    private static string Head(string text, int length) => text[..Math.Min(text.Length, length)];
}
