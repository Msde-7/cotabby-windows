using Cotabby.Core.Input;
using Cotabby.Core.Overlay;
using Cotabby.Core.Spelling;
using Cotabby.Core.Support;

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

    /// <summary>Quiet time after a key before predicting, used until a generation has been timed.</summary>
    public int DebounceMs { get; init; } = 20;

    /// <summary>True when requests go to a configured endpoint instead of the local model.</summary>
    public bool UsesEndpoint { get; init; }

    public SuggestionWordRange WordRange { get; init; } = new(12, 20);

    public bool AllowMultiLine { get; init; }

    public KeyBinding AcceptWord { get; init; } = KeyBinding.Tab;
    public KeyBinding AcceptAll { get; init; } = KeyBinding.Backtick;
    public IReadOnlyList<PerAppShortcutOverride> ShortcutOverrides { get; init; } = [];

    /// <summary>A quick second press of the accept-word key takes the rest of the suggestion.</summary>
    public bool DoubleTapAcceptsAll { get; init; }

    public AcceptanceGranularity Granularity { get; init; }
    public bool AcceptPunctuationWithWord { get; init; } = true;
    public bool AddSpaceAfterAccept { get; init; }

    public SuggestionDisplayMode DisplayMode { get; init; }

    /// <summary>When off, a new suggestion waits for the word being typed to be finished.</summary>
    public bool SuggestWithinWords { get; init; } = true;

    public TypoGateSettings Typos { get; init; } = new(
        SuppressCompletionsOnTypo: true, OfferTypoCorrections: true, AutomaticallyFixTypos: false);
    public IReadOnlyList<string> SpellingDictionaries { get; init; } = SpellingDictionaryCatalog.DefaultEnabledCodes;

    public string UserName { get; init; } = "";
    public IReadOnlyList<string> Languages { get; init; } = LanguageCatalog.DefaultLanguages;

    /// <summary>Notes about the writer that go into every prompt.</summary>
    public string ExtendedContext { get; init; } = "";

    /// <summary>Lets recently copied text that relates to the writing into the prompt.</summary>
    public bool ClipboardContext { get; init; }

    /// <summary>Tells the model what kind of app and window the text is written in.</summary>
    public bool SurfaceContext { get; init; } = true;

    /// <summary>
    /// Token budget for a request, sized for the upper word bound in the user's languages. It is
    /// doubled and capped when multi-line is on.
    /// </summary>
    public int MaxTokensFor(bool singleLine)
    {
        int single = Math.Max(MinTokens, SuggestionWordRange.PredictionTokenBudget(
            WordRange.HighWords, LanguageCatalog.EffectiveTokensPerWord(Languages)));
        return singleLine || !AllowMultiLine ? single : Math.Min(single * 2, MaxMultiLineTokens);
    }

    public const int MaxMultiLineTokens = 120;

    /// <summary>Smallest budget a request may get, sized for the 2 to 4 word preset.</summary>
    public const int MinTokens = 5;
}
