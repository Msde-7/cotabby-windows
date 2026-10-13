namespace Cotabby.Core.Spelling;

/// <summary>
/// A bundled SymSpell frequency dictionary. Declaration order is the stable catalog order used
/// for persisted codes and for rendering.
/// </summary>
public enum SpellingDictionaryLanguage
{
    English,
    German,
    Spanish,
    French,
    Hebrew,
    Italian,
    Russian,
}

public static class SpellingDictionaryLanguageExtensions
{
    /// <summary>The ISO 639-1 code persisted in settings.</summary>
    public static string Code(this SpellingDictionaryLanguage language) => language switch
    {
        SpellingDictionaryLanguage.English => "en",
        SpellingDictionaryLanguage.German => "de",
        SpellingDictionaryLanguage.Spanish => "es",
        SpellingDictionaryLanguage.French => "fr",
        SpellingDictionaryLanguage.Hebrew => "he",
        SpellingDictionaryLanguage.Italian => "it",
        SpellingDictionaryLanguage.Russian => "ru",
        _ => throw new ArgumentOutOfRangeException(nameof(language)),
    };

    /// <summary>English name used in logs.</summary>
    public static string DisplayName(this SpellingDictionaryLanguage language) => language.ToString();

    /// <summary>Native-script label shown in Settings so speakers can find their language quickly.</summary>
    public static string SettingsLabel(this SpellingDictionaryLanguage language) => language switch
    {
        SpellingDictionaryLanguage.English => "English",
        SpellingDictionaryLanguage.German => "Deutsch (German)",
        SpellingDictionaryLanguage.Spanish => "Español (Spanish)",
        SpellingDictionaryLanguage.French => "Français (French)",
        SpellingDictionaryLanguage.Hebrew => "עברית (Hebrew)",
        SpellingDictionaryLanguage.Italian => "Italiano (Italian)",
        SpellingDictionaryLanguage.Russian => "Русский (Russian)",
        _ => throw new ArgumentOutOfRangeException(nameof(language)),
    };

    /// <summary>Resource basename from the SymSpell frequency-dictionary folder.</summary>
    public static string ResourceName(this SpellingDictionaryLanguage language) => language switch
    {
        SpellingDictionaryLanguage.English => "frequency_dictionary_en_82_765",
        SpellingDictionaryLanguage.German => "de-100k",
        // SymSpell ships the Spanish list as "es-100l" with a letter l, and the bundled file keeps that name.
        SpellingDictionaryLanguage.Spanish => "es-100l",
        SpellingDictionaryLanguage.French => "fr-100k",
        SpellingDictionaryLanguage.Hebrew => "he-100k",
        SpellingDictionaryLanguage.Italian => "it-100k",
        SpellingDictionaryLanguage.Russian => "ru-100k",
        _ => throw new ArgumentOutOfRangeException(nameof(language)),
    };
}

/// <summary>Pure catalog rules for the spelling-dictionary setting.</summary>
public static class SpellingDictionaryCatalog
{
    public static IReadOnlyList<SpellingDictionaryLanguage> AllLanguages { get; } =
        Enum.GetValues<SpellingDictionaryLanguage>();

    public static IReadOnlyList<string> DefaultEnabledCodes { get; } = [SpellingDictionaryLanguage.English.Code()];

    /// <summary>Drops unknown and duplicate codes and returns the rest in catalog order.</summary>
    public static IReadOnlyList<string> Normalize(IEnumerable<string> codes) =>
        Languages(codes).Select(language => language.Code()).ToArray();

    public static IReadOnlyList<SpellingDictionaryLanguage> Languages(IEnumerable<string> codes)
    {
        var requested = codes.Select(code => code.Trim().ToLowerInvariant()).ToHashSet();
        return AllLanguages.Where(language => requested.Contains(language.Code())).ToArray();
    }
}
