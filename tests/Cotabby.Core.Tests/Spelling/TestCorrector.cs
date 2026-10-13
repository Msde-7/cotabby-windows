using Cotabby.Core.Spelling;

namespace Cotabby.Core.Tests.Spelling;

/// <summary>Builds correctors over in-memory dictionaries, so no bundled word list is read.</summary>
internal static class TestCorrector
{
    private static readonly TimeSpan LoadTimeout = TimeSpan.FromSeconds(60);

    /// <summary>A corrector that can read the given dictionaries and has loaded none of them.</summary>
    public static SymSpellCorrector Cold(params (SpellingDictionaryLanguage Language, string Contents)[] dictionaries)
    {
        var contents = dictionaries.ToDictionary(dictionary => dictionary.Language, dictionary => dictionary.Contents);
        return new SymSpellCorrector(resourceLoader: language => contents.GetValueOrDefault(language));
    }

    public static SymSpellCorrector Loaded(params (SpellingDictionaryLanguage Language, string Contents)[] dictionaries)
    {
        var corrector = Cold(dictionaries);
        foreach (var (language, _) in dictionaries) Load(corrector, language);
        return corrector;
    }

    /// <summary>
    /// Starts the background load and waits for it. A cold dictionary vouches for every word, so
    /// the load is done once a nonsense word is no longer known.
    /// </summary>
    public static void Load(SymSpellCorrector corrector, SpellingDictionaryLanguage language) =>
        Assert.True(
            SpinWait.SpinUntil(() => !corrector.IsKnownWord("qqzzxxqq", language), LoadTimeout),
            $"The {language} dictionary never loaded.");
}
