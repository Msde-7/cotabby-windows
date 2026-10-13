using Cotabby.Core.Support;

namespace Cotabby.Core.Suggestions;

/// <summary>
/// One suggestion request from the coordinator to an <see cref="ISuggestionEngine"/>, built by
/// <see cref="SuggestionRequestFactory"/> from a focus snapshot.
/// </summary>
public sealed record SuggestionRequest
{
    /// <summary>Correlation id that joins the coordinator, engine and inserter log lines.</summary>
    public required string RequestId { get; init; }

    /// <summary>The windowed text before the caret, which the engine renders into the prompt.</summary>
    public required string Prefix { get; init; }

    /// <summary>The windowed text after the caret. Drives fill-in-middle and the trailing-duplication filter.</summary>
    public required string Suffix { get; init; }

    /// <summary>True when the suggestion must stay on one line.</summary>
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
