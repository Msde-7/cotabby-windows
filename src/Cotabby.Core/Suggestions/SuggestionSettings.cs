using Cotabby.Core.Input;
using Cotabby.Core.Spelling;

namespace Cotabby.Core.Suggestions;

/// <summary>How much of a suggestion the accept-word key takes per press.</summary>
public enum AcceptanceGranularity
{
    Word,
    Phrase,
}

/// <summary>
/// Immutable snapshot of every preference the suggestion pipeline reads. The app
/// builds a new one whenever settings change, so work already in flight never
/// sees a half-applied edit.
/// </summary>
public sealed record SuggestionSettings
{
    public static SuggestionSettings Default { get; } = new();

    public bool Enabled { get; init; } = true;
    public PauseState? Pause { get; init; }
    public bool PauseOnBatterySaver { get; init; } = true;

    /// <summary>Process names, without extension, where suggestions are off.</summary>
    public IReadOnlySet<string> BlockedApps { get; init; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public bool SuggestInIntegratedTerminals { get; init; }

    /// <summary>Token budget for one single-line suggestion.</summary>
    public int MaxTokens { get; init; } = 26;

    public bool AllowMultiLine { get; init; }

    public KeyBinding AcceptWord { get; init; } = KeyBinding.Tab;
    public KeyBinding AcceptAll { get; init; } = KeyBinding.Backtick;
    public IReadOnlyList<PerAppShortcutOverride> ShortcutOverrides { get; init; } = [];

    /// <summary>A quick second press of the accept-word key takes the rest of the suggestion.</summary>
    public bool DoubleTapAcceptsAll { get; init; }

    public AcceptanceGranularity Granularity { get; init; }
    public bool AcceptPunctuationWithWord { get; init; } = true;
    public bool AddSpaceAfterAccept { get; init; }

    /// <summary>When off, a new suggestion waits for the word being typed to be finished.</summary>
    public bool SuggestWithinWords { get; init; } = true;

    public TypoGateSettings Typos { get; init; } = new(
        SuppressCompletionsOnTypo: true, OfferTypoCorrections: true, AutomaticallyFixTypos: false);
    public IReadOnlyList<string> SpellingDictionaries { get; init; } = SpellingDictionaryCatalog.DefaultEnabledCodes;

    public string UserName { get; init; } = "";
    public IReadOnlyList<string> Languages { get; init; } = LanguageCatalog.DefaultLanguages;

    /// <summary>Token budget for a request, doubled and capped when multi-line is on.</summary>
    public int MaxTokensFor(bool singleLine) =>
        singleLine || !AllowMultiLine ? MaxTokens : Math.Min(MaxTokens * 2, MaxMultiLineTokens);

    public const int MaxMultiLineTokens = 120;

    /// <summary>Smallest budget a request may get, sized for the 2 to 4 word preset.</summary>
    public const int MinTokens = 5;

    /// <summary>
    /// The single-line token budget for a word range in the user's languages, rounded
    /// up so the cap lands at or just past the upper word bound.
    /// </summary>
    public static int TokenBudget(int highWords, IEnumerable<string> languages) =>
        Math.Max(MinTokens, (int)Math.Ceiling(highWords * LanguageCatalog.EffectiveTokensPerWord(languages)));
}
