namespace Cotabby.Core.Suggestions;

/// <summary>One entry in the suggested-language palette.</summary>
/// <param name="Name">Canonical English name. This is what is stored and what goes into the prompt.</param>
/// <param name="NativeLabel">Native-script label shown in the palette.</param>
/// <param name="TokensPerWord">Average tokens per word, which drives the per-language token budget.</param>
public sealed record LanguageOption(string Name, string NativeLabel, double TokensPerWord);

/// <summary>
/// The languages a user can say they write in. Stored as free text names so any
/// language can be added. <see cref="Normalize"/> is the one place that trims,
/// de-duplicates and caps them.
/// </summary>
public static class LanguageCatalog
{
    public const int MaxLanguages = 6;
    public const int MaxLanguageLength = 30;

    /// <summary>Used when no single recognized language is selected.</summary>
    public const double FallbackTokensPerWord = 1.3;

    public static IReadOnlyList<string> DefaultLanguages { get; } = ["English"];

    public static IReadOnlyList<LanguageOption> CommonLanguages { get; } =
    [
        new("English", "English", 1.3),
        new("Spanish", "Español (Spanish)", 1.5),
        new("French", "Français (French)", 1.5),
        new("German", "Deutsch (German)", 1.7),
        new("Italian", "Italiano (Italian)", 1.5),
        new("Portuguese", "Português (Portuguese)", 1.5),
        new("Dutch", "Nederlands (Dutch)", 1.6),
        new("Russian", "Русский (Russian)", 2.0),
        new("Simplified Chinese", "简体中文 (Simplified Chinese)", 1.2),
        new("Japanese", "日本語 (Japanese)", 1.8),
        new("Korean", "한국어 (Korean)", 1.8),
        new("Hindi", "हिन्दी (Hindi)", 2.0),
        new("Arabic", "العربية (Arabic)", 2.0),
    ];

    /// <summary>
    /// The tokens-per-word factor for the declared languages. Only a single
    /// recognized language gets its own factor. Anything else gets the fallback.
    /// </summary>
    public static double EffectiveTokensPerWord(IEnumerable<string> languages)
    {
        var normalized = Normalize(languages);
        if (normalized.Count != 1) return FallbackTokensPerWord;
        var option = CommonLanguages.FirstOrDefault(
            o => o.Name.Equals(normalized[0], StringComparison.OrdinalIgnoreCase));
        return option?.TokensPerWord ?? FallbackTokensPerWord;
    }

    public static List<string> Normalize(IEnumerable<string> languages)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var language in languages)
        {
            var trimmed = language.Trim();
            if (trimmed.Length == 0) continue;
            if (trimmed.Length > MaxLanguageLength) trimmed = trimmed[..MaxLanguageLength];
            if (!seen.Add(trimmed)) continue;
            result.Add(trimmed);
            if (result.Count >= MaxLanguages) break;
        }
        return result;
    }
}
