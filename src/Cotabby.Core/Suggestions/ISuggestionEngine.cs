namespace Cotabby.Core.Suggestions;

/// <summary>
/// A suggestion-generation backend. The contract is intentionally minimal so
/// the coordinator can swap llama.cpp for an ONNX engine, a hosted API, or a
/// mock during tests without touching the state machine.
/// </summary>
public interface ISuggestionEngine
{
    /// <summary>Is the engine ready (weights loaded, runtime warm)?</summary>
    bool IsReady { get; }

    /// <summary>
    /// Stream suggestion chunks. Implementations stop on EOS, max-token cap,
    /// anti-prompt match, or cancellation, and must surface chunks promptly
    /// so the overlay can render token-by-token instead of waiting for the
    /// full completion.
    /// </summary>
    IAsyncEnumerable<SuggestionChunk> GenerateAsync(SuggestionRequest request, CancellationToken ct);
}
