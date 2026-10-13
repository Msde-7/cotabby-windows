using Cotabby.Core.Support;

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

    /// <summary>The raw text preceding the caret (windowed to ~1000 chars).
    /// The engine word-windows and renders this into the prompt.</summary>
    public required string Prefix { get; init; }

    /// <summary>Raw text following the caret (windowed). Drives fill-in-middle
    /// and the trailing-duplication filter.</summary>
    public required string Suffix { get; init; }

    /// <summary>Host process name. Used for app-specific style hints.</summary>
    public required string HostApp { get; init; }

    /// <summary>True if the field is single-line — engines should clamp to one line.</summary>
    public required bool SingleLine { get; init; }

    /// <summary>Maximum number of tokens the engine should emit.</summary>
    public required int MaxTokens { get; init; }

    /// <summary>The user's name for the persona conditioning line, or empty.</summary>
    public string UserName { get; init; } = "";

    /// <summary>Language conditioning sentence for the prompt preface, or empty.</summary>
    public string LanguageInstruction { get; init; } = "";

    /// <summary>Notes about the writer that the model keeps in mind, or empty.</summary>
    public string ExtendedContext { get; init; } = "";

    /// <summary>Recently copied text that relates to what is being written, or empty.</summary>
    public string ClipboardContext { get; init; } = "";

    /// <summary>Lines describing the app and field being written in.</summary>
    public IReadOnlyList<string> SurfaceLines { get; init; } = [];

    /// <summary>How many words the suggestion should run to.</summary>
    public SuggestionWordRange? WordRange { get; init; }
}

/// <summary>One incremental suggestion chunk streamed back from the engine.</summary>
public readonly record struct SuggestionChunk(string Text, bool IsFinal);
