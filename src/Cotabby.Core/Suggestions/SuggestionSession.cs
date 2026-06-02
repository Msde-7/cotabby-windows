using Cotabby.Core.Focus;

namespace Cotabby.Core.Suggestions;

/// <summary>
/// The state of one in-flight or visible suggestion. Immutable — every state
/// transition (token arrival, user keystroke, accept, cancel) produces a fresh
/// instance, so the coordinator can compare-and-swap without races against the
/// generation task.
/// </summary>
public sealed record SuggestionSession
{
    /// <summary>Correlation id mirrored from <see cref="SuggestionRequest.RequestId"/>.</summary>
    public required string RequestId { get; init; }

    /// <summary>Element handle of the field this session targets.</summary>
    public required object ElementHandle { get; init; }

    /// <summary>The full prefix as it stood when the session began.</summary>
    public required string OriginalPrefix { get; init; }

    /// <summary>The characters the user has typed since the session began that
    /// matched the suggestion. Stored as a count, not a copy of the substring,
    /// because the live <see cref="VisibleText"/> already encodes the same fact.</summary>
    public required int ConsumedChars { get; init; }

    /// <summary>The portion of the streamed suggestion still pending presentation.</summary>
    public required string VisibleText { get; init; }

    /// <summary>True once the engine signaled the final chunk.</summary>
    public required bool IsComplete { get; init; }

    /// <summary>Anchor rect (caret screen rect at request time) used by the overlay.</summary>
    public required ScreenRect AnchorRect { get; init; }
}
