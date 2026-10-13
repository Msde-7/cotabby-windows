using System.Collections.Concurrent;
using System.Text;
using Cotabby.Core.Text;

namespace Cotabby.Core.Spelling;

/// <summary>
/// Selects one enabled spelling dictionary from the text around the current typo. The resolver
/// is deliberately conservative because a wrong dictionary can produce a fluent but destructive
/// correction, while returning null only skips the correction. A single enabled dictionary is
/// the user's explicit choice and wins without detection. With several enabled, the text must
/// identify one with sufficient confidence.
/// </summary>
public sealed class SpellingLanguageResolver(Func<SpellingDictionaryLanguage, string?>? resourceLoader = null)
{
    // Enough recent prose for reliable identification without scanning a large editor buffer on
    // the typing path. Upstream counts grapheme clusters here and this counts UTF-16 units.
    private const int MaximumContextCharacters = 800;

    // Short Latin-script words are often ambiguous across languages. Requiring a majority keeps
    // such a word from selecting an arbitrary enabled dictionary.
    private const double MinimumConfidence = 0.55;

    private readonly LanguageVocabularyScorer _scorer = new(resourceLoader ?? SpellingDictionaryResources.Loader());

    /// <summary>
    /// Returns the one enabled dictionary appropriate for <paramref name="precedingText"/>, or null
    /// when the language is ambiguous.
    /// </summary>
    public SpellingDictionaryLanguage? Resolve(
        string precedingText, string currentWord, IReadOnlyList<SpellingDictionaryLanguage> enabledLanguages)
    {
        if (enabledLanguages.Count <= 1) return enabledLanguages.Count == 0 ? null : enabledLanguages[0];

        var sample = ContextSample(precedingText, currentWord);
        return sample.Length == 0 ? null : ConfidentLanguage(_scorer.Score(sample, enabledLanguages));
    }

    /// <summary>The pure selection rule. The best score wins only when it reaches the confidence threshold.</summary>
    public static SpellingDictionaryLanguage? ConfidentLanguage(
        IReadOnlyDictionary<SpellingDictionaryLanguage, double> scores)
    {
        if (scores.Count == 0) return null;
        var best = scores.MaxBy(score => score.Value);
        return best.Value >= MinimumConfidence ? best.Key : null;
    }

    /// <summary>
    /// Removes the known typo from the end so a malformed current word cannot outweigh the valid
    /// sentence before it. With no earlier context the word itself remains useful for languages
    /// with a distinct script, such as Hebrew and Russian.
    /// </summary>
    private static string ContextSample(string precedingText, string currentWord)
    {
        var context = precedingText.EndsWith(currentWord, StringComparison.Ordinal)
            ? precedingText[..^currentWord.Length]
            : precedingText;
        context = context.Trim();
        var source = context.Length == 0 ? currentWord : context;
        return source[Math.Max(0, source.Length - MaximumContextCharacters)..];
    }
}

/// <summary>
/// Scores how strongly a text sample belongs to each enabled language. The macOS app asks Apple's
/// language recognizer, which has no Windows counterpart, so the bundled frequency dictionaries
/// supply the evidence. Each word of the sample votes for the enabled languages written in its
/// script, split by how common the word is in each one. A word found in none of them still votes
/// when only one enabled language uses its script. A score is a language's share of the votes.
/// </summary>
internal sealed class LanguageVocabularyScorer(Func<SpellingDictionaryLanguage, string?> resourceLoader)
{
    // The dictionaries are sorted by frequency, so the head holds the words that carry evidence.
    private const int VocabularySize = 20_000;

    private enum Script { Latin, Cyrillic, Hebrew, Other }

    private readonly ConcurrentDictionary<SpellingDictionaryLanguage, Dictionary<string, double>> _vocabularies = new();

    public IReadOnlyDictionary<SpellingDictionaryLanguage, double> Score(
        string sample, IReadOnlyList<SpellingDictionaryLanguage> languages)
    {
        var votes = languages.ToDictionary(language => language, _ => 0.0);
        int voters = 0;
        foreach (var word in Words(sample))
        {
            var script = ScriptOf(word);
            var writers = languages.Where(language => ScriptOf(language) == script).ToArray();
            var shares = writers.Select(language => Vocabulary(language).GetValueOrDefault(word)).ToArray();
            double total = shares.Sum();
            if (total == 0 && writers.Length != 1) continue;

            voters++;
            for (int index = 0; index < writers.Length; index++)
            {
                votes[writers[index]] += total == 0 ? 1 : shares[index] / total;
            }
        }
        return votes.ToDictionary(vote => vote.Key, vote => voters == 0 ? 0 : vote.Value / voters);
    }

    /// <summary>The relative frequency of each word in the head of a language's dictionary.</summary>
    private Dictionary<string, double> Vocabulary(SpellingDictionaryLanguage language) =>
        _vocabularies.GetOrAdd(language, _ =>
        {
            var entries = FrequencyDictionaryFormat.Entries(resourceLoader(language) ?? "")
                .Where(entry => entry.Count > 0)
                .Take(VocabularySize)
                .DistinctBy(entry => entry.Word)
                .ToArray();
            double total = entries.Sum(entry => (double)entry.Count);
            return entries.ToDictionary(entry => entry.Word, entry => entry.Count / total, StringComparer.Ordinal);
        });

    private static IEnumerable<string> Words(string sample)
    {
        var text = FrequencyDictionaryFormat.Canonical(sample).ToLowerInvariant();
        return new GraphemeString(text).Split(c => !c.IsLetter).Select(range => text[range]);
    }

    private static Script ScriptOf(SpellingDictionaryLanguage language) => language switch
    {
        SpellingDictionaryLanguage.Russian => Script.Cyrillic,
        SpellingDictionaryLanguage.Hebrew => Script.Hebrew,
        _ => Script.Latin,
    };

    private static Script ScriptOf(string word) => Rune.GetRuneAt(word, 0).Value switch
    {
        < 0x0250 or (>= 0x1E00 and <= 0x1EFF) => Script.Latin,
        >= 0x0400 and <= 0x052F => Script.Cyrillic,
        >= 0x0590 and <= 0x05FF => Script.Hebrew,
        _ => Script.Other,
    };
}
