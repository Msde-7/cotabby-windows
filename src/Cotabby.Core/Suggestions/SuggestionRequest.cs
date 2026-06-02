namespace Cotabby.Core.Suggestions;

/// <summary>
/// A single suggestion request flowing from the coordinator into an
/// <see cref="ISuggestionEngine"/>. Built by <c>SuggestionRequestFactory</c>
/// in <see cref="Cotabby.Core.Suggestions.SuggestionRequestFactory"/> from
/// a focus snapshot plus the most recent typed text. Pure value object —
/// no references into UIA, no async work yet.
/// </summary>
public sealed record SuggestionRequest
{
    /// <summary>Correlation id for log joining across coordinator → engine → host.</summary>
    public required string RequestId { get; init; }

    /// <summary>The full prefix that should precede the suggestion.</summary>
    public required string Prefix { get; init; }

    /// <summary>Best-effort suffix for fill-in-middle prompts. Empty if unavailable.</summary>
    public required string Suffix { get; init; }

    /// <summary>Host process name. Used for app-specific style hints.</summary>
    public required string HostApp { get; init; }

    /// <summary>True if the field is single-line — engines should clamp to one line.</summary>
    public required bool SingleLine { get; init; }

    /// <summary>Maximum number of tokens the engine should emit.</summary>
    public required int MaxTokens { get; init; }
}

/// <summary>One incremental suggestion chunk streamed back from the engine.</summary>
public readonly record struct SuggestionChunk(string Text, bool IsFinal);
