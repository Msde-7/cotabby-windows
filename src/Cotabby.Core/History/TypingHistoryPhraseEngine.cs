using Cotabby.Core.Suggestions;

namespace Cotabby.Core.History;

/// <summary>
/// Answers a request straight from typing history when history knows how the phrase ends, and
/// otherwise hands it to the real engine.
/// <para>
/// A phrase shortcut is just another way to produce a completion, so the coordinator, overlay and
/// acceptance code need no changes to use it. Sitting in front of the router means one check covers
/// every engine, and the live engine is read per request so the endpoint never gets history text.
/// </para>
/// </summary>
public sealed class TypingHistoryPhraseEngine(
    ISuggestionEngine inner, ISuggestionHistoryProvider history, Func<bool> usesEndpoint) : ISuggestionEngine
{
    public bool IsReady => inner.IsReady;

    /// <summary>
    /// A phrase is the user's own text, character for character, which matches the local engine's
    /// exact spacing. The endpoint never gets a phrase, so its spacing rule is unchanged.
    /// </summary>
    public bool SpacingIsExact => inner.SpacingIsExact;

    public Task<string> GenerateAsync(SuggestionRequest request, CancellationToken ct, Action<string>? onPartial = null)
    {
        if (history.PhraseContinuation(request, usesEndpoint()) is { } phrase) return Task.FromResult(phrase);
        return inner.GenerateAsync(request, ct, onPartial);
    }
}
