using Cotabby.Core.Focus;

namespace Cotabby.Core.Suggestions;

/// <summary>
/// One visible suggestion. Immutable, so every transition (typing through it,
/// accepting part of it) produces a fresh instance.
/// </summary>
public sealed record SuggestionSession
{
    public required string RequestId { get; init; }

    /// <summary>The part of the suggestion not yet typed or accepted.</summary>
    public required string VisibleText { get; init; }

    /// <summary>Caret rect the overlay is anchored to.</summary>
    public required ScreenRect AnchorRect { get; init; }
}
