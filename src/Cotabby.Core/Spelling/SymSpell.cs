using System.Globalization;
using System.Runtime.InteropServices;
using Cotabby.Core.Text;

namespace Cotabby.Core.Spelling;

// C# port of SymSpell, Wolf Garbe's Symmetric Delete spelling correction, used under the MIT License.
//
//   Copyright (c) 2018 Wolf Garbe (https://github.com/wolfgarbe/SymSpell)
//
//   Permission is hereby granted, free of charge, to any person obtaining a copy of this software
//   and associated documentation files (the "Software"), to deal in the Software without
//   restriction, including without limitation the rights to use, copy, modify, merge, publish,
//   distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the
//   Software is furnished to do so, subject to the following conditions: The above copyright
//   notice and this permission notice shall be included in all copies or substantial portions of
//   the Software. THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND.
//
// The bundled frequency_dictionary_en_82_765.txt also ships with SymSpell and is derived from
// Google Books Ngram data (CC BY 3.0) and SCOWL.

public readonly record struct SymSpellSuggestion(string Term, int Distance, long Count);

/// <summary>
/// Returns the most frequent dictionary word within a small edit distance of a query in roughly
/// constant time. Every dictionary word is reduced by up to <see cref="MaxDictionaryEditDistance"/>
/// single-character deletions into a delete index, and a query is matched by reducing it the same
/// way and intersecting. Characters are grapheme clusters, as in the macOS source.
///
/// Not thread safe while loading. Once loading has finished the index is only read, so any number
/// of threads may call <see cref="Lookup"/>.
/// </summary>
public sealed class SymSpell(int maxDictionaryEditDistance = 2, int prefixLength = 7)
{
    private readonly Dictionary<string, long> _words = new(StringComparer.Ordinal);
    private readonly List<string> _wordsList = [];

    // FNV-1a hash of a delete variant to the indices of the words that produce it. Collisions are
    // harmless because every candidate is verified with a real bounded edit-distance check.
    private readonly Dictionary<ulong, Bucket> _deletes = [];

    /// <summary>
    /// The word indices behind one delete hash. The array grows by doubling, so adding a word is
    /// amortized constant time even for the very common short deletes, where copying the whole
    /// bucket on every add would make loading quadratic in the bucket size.
    /// </summary>
    private struct Bucket
    {
        public int[] Items;
        public int Count;

        public void Add(int index)
        {
            if (Items is null) Items = new int[1];
            else if (Count == Items.Length) Array.Resize(ref Items, Count * 2);
            Items[Count++] = index;
        }

        public readonly ReadOnlySpan<int> Indices => Items.AsSpan(0, Count);
    }
    private int _maxDictionaryWordLength;

    public int MaxDictionaryEditDistance { get; } = maxDictionaryEditDistance;

    public int PrefixLength { get; } = prefixLength;

    public int WordCount => _wordsList.Count;

    public bool Contains(string word) => _words.ContainsKey(FrequencyDictionaryFormat.Canonical(word));

    /// <summary>
    /// Parses <c>word count</c> lines separated by a space or a tab and skips malformed ones.
    /// Building the index is the expensive part, so call this off the UI thread.
    /// </summary>
    public void LoadDictionary(string contents, CancellationToken cancellationToken = default)
    {
        foreach (var (word, count) in FrequencyDictionaryFormat.Entries(contents))
        {
            cancellationToken.ThrowIfCancellationRequested();
            CreateDictionaryEntry(word, count);
        }
        // Growth slack is dropped once the index stops changing.
        foreach (ulong key in _deletes.Keys)
        {
            ref var bucket = ref CollectionsMarshal.GetValueRefOrNullRef(_deletes, key);
            if (bucket.Count < bucket.Items.Length) Array.Resize(ref bucket.Items, bucket.Count);
        }
        _deletes.TrimExcess();
    }

    public void CreateDictionaryEntry(string key, long count)
    {
        if (count <= 0 || !_words.TryAdd(key, count)) return;
        int index = _wordsList.Count;
        _wordsList.Add(key);

        var characters = Characters(key);
        _maxDictionaryWordLength = Math.Max(_maxDictionaryWordLength, characters.Length);
        foreach (ulong delete in DeleteHashes(characters))
        {
            CollectionsMarshal.GetValueRefOrAddDefault(_deletes, delete, out _).Add(index);
        }
    }

    /// <summary>
    /// Returns dictionary words within <paramref name="maxEditDistance"/> of <paramref name="input"/>,
    /// lowest distance first and then highest frequency. An exact match short-circuits to distance 0.
    /// A distance above the one the index was built with is capped at it.
    /// </summary>
    public IReadOnlyList<SymSpellSuggestion> Lookup(string input, int? maxEditDistance = null)
    {
        input = FrequencyDictionaryFormat.Canonical(input);
        int maxDistance = Math.Min(maxEditDistance ?? MaxDictionaryEditDistance, MaxDictionaryEditDistance);
        var inputCharacters = Characters(input);

        // Too long to be within the distance of any dictionary word.
        if (inputCharacters.Length - maxDistance > _maxDictionaryWordLength) return [];
        if (_words.TryGetValue(input, out long exactCount)) return [new(input, 0, exactCount)];
        if (maxDistance == 0) return [];

        var suggestions = new List<SymSpellSuggestion>();
        var consideredSuggestions = new HashSet<string>(StringComparer.Ordinal);
        var consideredDeletes = new HashSet<string>(StringComparer.Ordinal);
        bool inputIsOneUnitPerGrapheme = GraphemeString.IsOneUnitPerGrapheme(input);

        int inputPrefixLength = Math.Min(inputCharacters.Length, PrefixLength);
        var candidates = new List<string[]> { inputCharacters[..inputPrefixLength] };
        for (int pointer = 0; pointer < candidates.Count; pointer++)
        {
            var candidate = candidates[pointer];
            if (_deletes.TryGetValue(Hash(candidate), out var bucket))
            {
                foreach (int wordIndex in bucket.Indices)
                {
                    var suggestion = _wordsList[wordIndex];
                    int distance;
                    // When both words are one UTF-16 unit per grapheme, compare units and skip
                    // splitting the word into grapheme strings.
                    if (inputIsOneUnitPerGrapheme && GraphemeString.IsOneUnitPerGrapheme(suggestion))
                    {
                        // Cheap length prune before the quadratic distance computation.
                        if (Math.Abs(suggestion.Length - input.Length) > maxDistance) continue;
                        if (!consideredSuggestions.Add(suggestion)) continue;
                        distance = EditDistance.DamerauOsa(input.AsSpan(), suggestion.AsSpan(), maxDistance);
                    }
                    else
                    {
                        var suggestionCharacters = Characters(suggestion);
                        if (Math.Abs(suggestionCharacters.Length - inputCharacters.Length) > maxDistance) continue;
                        if (!consideredSuggestions.Add(suggestion)) continue;
                        distance = DamerauOsa(inputCharacters, suggestionCharacters, maxDistance);
                    }
                    if (distance >= 0) suggestions.Add(new(suggestion, distance, _words[suggestion]));
                }
            }

            // Enqueue the candidate's own deletes, bounded by the prefix window and the distance.
            if (inputPrefixLength - candidate.Length < maxDistance && candidate.Length <= PrefixLength)
            {
                for (int index = 0; index < candidate.Length; index++)
                {
                    var deleted = Without(candidate, index);
                    if (consideredDeletes.Add(string.Concat(deleted))) candidates.Add(deleted);
                }
            }
        }

        return suggestions.OrderBy(s => s.Distance).ThenByDescending(s => s.Count).ToArray();
    }

    /// <summary>The single best correction for <paramref name="input"/>, or null when nothing is within the edit distance.</summary>
    public SymSpellSuggestion? BestSuggestion(string input) =>
        Lookup(input) is [var best, ..] ? best : null;

    /// <summary>
    /// Bounded Damerau optimal-string-alignment distance over grapheme clusters, which allows an
    /// adjacent transposition. Returns -1 when the distance exceeds <paramref name="maxDistance"/>.
    /// </summary>
    public static int DamerauOsa(ReadOnlySpan<string> source, ReadOnlySpan<string> target, int maxDistance) =>
        EditDistance.DamerauOsa(source, target, maxDistance);

    /// <summary>
    /// The hashes of all delete variants of a word's prefix, capped at <see cref="PrefixLength"/>,
    /// down to <see cref="MaxDictionaryEditDistance"/>, plus the empty string for words short
    /// enough to delete entirely. Only the hash of a variant is ever needed while indexing, so the
    /// variant strings themselves are not built.
    /// </summary>
    private HashSet<ulong> DeleteHashes(string[] key)
    {
        var result = new HashSet<ulong>();
        if (key.Length <= MaxDictionaryEditDistance) result.Add(Hash([]));
        var prefix = key.Length > PrefixLength ? key[..PrefixLength] : key;
        result.Add(Hash(prefix));
        AddDeleteHashes(prefix, 0, result);
        return result;
    }

    private void AddDeleteHashes(string[] word, int editDistance, HashSet<ulong> result)
    {
        if (word.Length <= 1) return;
        for (int index = 0; index < word.Length; index++)
        {
            var deleted = Without(word, index);
            if (result.Add(Hash(deleted)) && editDistance + 1 < MaxDictionaryEditDistance)
            {
                AddDeleteHashes(deleted, editDistance + 1, result);
            }
        }
    }

    private static string[] Characters(string text) => new GraphemeString(text).Select(c => c.ToString()).ToArray();

    private static string[] Without(string[] characters, int index)
    {
        var result = new string[characters.Length - 1];
        Array.Copy(characters, result, index);
        Array.Copy(characters, index + 1, result, index, result.Length - index);
        return result;
    }

    /// <summary>FNV-1a over UTF-8. The index is rebuilt each launch, so the hash need not be stable across processes.</summary>
    private static ulong Hash(ReadOnlySpan<string> characters)
    {
        ulong hash = 0xcbf2_9ce4_8422_2325;
        Span<byte> utf8 = stackalloc byte[4];
        foreach (var character in characters)
        {
            foreach (var scalar in character.EnumerateRunes())
            {
                foreach (byte unit in utf8[..scalar.EncodeToUtf8(utf8)])
                {
                    hash = (hash ^ unit) * 0x0000_0100_0000_01b3;
                }
            }
        }
        return hash;
    }
}

/// <summary>The SymSpell frequency-dictionary text format, one <c>word count</c> pair per line.</summary>
internal static class FrequencyDictionaryFormat
{
    /// <summary>
    /// Swift compares strings by canonical equivalence and .NET compares UTF-16 units. Typed text
    /// is composed to NFC before it meets the dictionaries, which are stored in NFC.
    /// </summary>
    public static string Canonical(string text)
    {
        try
        {
            return text.Normalize();
        }
        catch (ArgumentException)
        {
            // An unpaired surrogate cannot be normalized. Such text never matches a dictionary word.
            return text;
        }
    }

    /// <summary>
    /// Yields the first two fields of every line whose second field is an integer. Fields are
    /// separated by spaces or tabs.
    /// </summary>
    public static IEnumerable<(string Word, long Count)> Entries(string contents)
    {
        for (int start = 0; start < contents.Length;)
        {
            int end = contents.IndexOf('\n', start);
            if (end < 0) end = contents.Length;
            if (TryParse(contents.AsSpan(start, end - start), out var word, out long count))
            {
                yield return (word, count);
            }
            start = end + 1;
        }
    }

    private static bool TryParse(ReadOnlySpan<char> line, out string word, out long count)
    {
        word = "";
        count = 0;
        // The bundled files use LF. A checkout that rewrote them to CRLF must still parse.
        line = line.TrimEnd('\r');

        int wordStart = SkipSeparators(line, 0);
        int wordEnd = SkipField(line, wordStart);
        int countStart = SkipSeparators(line, wordEnd);
        int countEnd = SkipField(line, countStart);
        if (countStart == countEnd || !long.TryParse(
            line[countStart..countEnd], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out count))
        {
            return false;
        }
        word = line[wordStart..wordEnd].ToString();
        return true;

        bool IsSeparator(char c) => c is ' ' or '\t';

        int SkipSeparators(ReadOnlySpan<char> text, int index)
        {
            while (index < text.Length && IsSeparator(text[index])) index++;
            return index;
        }

        int SkipField(ReadOnlySpan<char> text, int index)
        {
            while (index < text.Length && !IsSeparator(text[index])) index++;
            return index;
        }
    }
}
