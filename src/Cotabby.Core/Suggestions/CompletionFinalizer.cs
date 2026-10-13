using Cotabby.Core.Focus;
using Cotabby.Core.Spelling;
using Cotabby.Core.Support;

namespace Cotabby.Core.Suggestions;

/// <summary>
/// The last checks on a finished completion, made against the text that is in the field now.
/// </summary>
public static class CompletionFinalizer
{
    /// <summary>
    /// The text to show, or null when the completion went stale or would join the typed word into
    /// a misspelling. The leading space is decided here because the user keeps typing while the
    /// model runs.
    /// </summary>
    public static string? VisibleText(
        string completion, FocusedField live, SuggestionSettings settings, bool spacingIsExact,
        TypoCorrectionAdvisor? typos)
    {
        string preceding = live.PrecedingText;
        if (GhostSpaceBoundary.IsStaleAfterTypedSpace(completion, preceding)) return null;

        string spaced = GhostSpaceBoundary.LiveAdjusted(completion, preceding, spacingIsExact ? preceding : null);
        var references = ReferenceVocabulary
            .Words(preceding, live.TrailingText, settings.ExtendedContext)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return CompletionSeamGuard.VisibleText(
            preceding, spaced,
            word => references.Contains(word) || typos is null
                ? CompletionSeamGuard.SpellingAssessment.Known
                : typos.Assess(word, preceding, settings.SpellingDictionaries));
    }
}
