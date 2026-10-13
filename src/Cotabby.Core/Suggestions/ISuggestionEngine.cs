namespace Cotabby.Core.Suggestions;

/// <summary>
/// A suggestion-generation backend. The contract is small so the coordinator can run against
/// the local model, an endpoint or a test double.
/// </summary>
public interface ISuggestionEngine
{
    /// <summary>True when the engine can generate.</summary>
    bool IsReady { get; }

    /// <summary>
    /// True when a completion is the exact text that follows the request's prefix, so its
    /// leading space or lack of one says whether it starts a new word.
    /// </summary>
    bool SpacingIsExact { get; }

    /// <summary>
    /// Yields the suggestion as chunks, ending with one whose <c>IsFinal</c> is set.
    /// Implementations stop on end of sequence, the token cap or cancellation.
    /// </summary>
    IAsyncEnumerable<SuggestionChunk> GenerateAsync(SuggestionRequest request, CancellationToken ct);
}
