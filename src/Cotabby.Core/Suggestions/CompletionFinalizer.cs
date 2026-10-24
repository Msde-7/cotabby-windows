using Cotabby.Core.Focus;
using Cotabby.Core.Spelling;
using Cotabby.Core.Support;

namespace Cotabby.Core.Suggestions;

/// <summary>A finished completion as it will be offered.</summary>
/// <param name="Visible">What shows first and may be accepted first.</param>
/// <param name="Predicted">
/// The whole prediction the session keeps, which begins with <paramref name="Visible"/>. Words
/// after a word ending stay hidden until the ending is typed or accepted.
/// </param>
public sealed record FinalCompletion(string Visible, string Predicted)
{
    /// <summary>A completion offered in full.</summary>
    public FinalCompletion(string text) : this(text, text)
    {
    }

    /// <summary>The first offer's length when part of the prediction waits behind it, or null.</summary>
    public int? FirstOfferLength => Predicted.Length > Visible.Length ? Visible.Length : null;
}

/// <summary>
/// The last checks on a finished completion, made against the text that is in the field now.
/// </summary>
public static class CompletionFinalizer
{
    /// <summary>
    /// What to offer for <paramref name="completion"/>, or null when it went stale or would join the
    /// typed word into a misspelling. The leading space is decided here because the user keeps
    /// typing while the model runs. When only a word ending passes, the rest of the prediction is
    /// kept if its own first word passes too.
    /// </summary>
    public static FinalCompletion? Finalize(
        string completion, FocusedField live, SuggestionSettings settings, bool spacingIsExact,
        TypoCorrectionAdvisor? typos)
    {
        string preceding = live.PrecedingText;
        if (GhostSpaceBoundary.IsStaleAfterTypedSpace(completion, preceding)) return null;

        string spaced = GhostSpaceBoundary.LiveAdjusted(completion, preceding, spacingIsExact ? preceding : null);
        var references = ReferenceVocabulary
            .Words(preceding, live.TrailingText, settings.ExtendedContext)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        CompletionSeamGuard.SpellingAssessment Assess(string word) =>
            references.Contains(word) || typos is null
                ? CompletionSeamGuard.SpellingAssessment.Known
                : typos.Assess(word, preceding, settings.SpellingDictionaries);

        if (CompletionSeamGuard.VisibleText(preceding, spaced, Assess) is not { } shown) return null;

        // The check that approved a name's ending did not approve "teh" after it, so the
        // following words are judged on their own before they are kept.
        bool keepsFollowing = spaced.Length > shown.Length && spaced.StartsWith(shown, StringComparison.Ordinal)
            && CompletionSeamGuard.VisibleText(preceding + shown + " ", spaced[shown.Length..], Assess) is not null;
        return new FinalCompletion(shown, keepsFollowing ? spaced : shown);
    }
}
