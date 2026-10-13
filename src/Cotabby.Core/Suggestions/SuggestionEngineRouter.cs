namespace Cotabby.Core.Suggestions;

/// <summary>
/// Sends each request to the engine the user selected. A failing endpoint never
/// falls back to the local model, because the user chose where their text goes.
/// </summary>
public sealed class SuggestionEngineRouter : ISuggestionEngine
{
    private readonly ISuggestionEngine _local;
    private readonly ISuggestionEngine _endpoint;
    private volatile bool _useEndpoint;

    public SuggestionEngineRouter(ISuggestionEngine local, ISuggestionEngine endpoint)
    {
        _local = local;
        _endpoint = endpoint;
    }

    public bool UseEndpoint
    {
        get => _useEndpoint;
        set => _useEndpoint = value;
    }

    public bool IsReady => Active.IsReady;

    public bool SpacingIsExact => Active.SpacingIsExact;

    public IAsyncEnumerable<SuggestionChunk> GenerateAsync(SuggestionRequest request, CancellationToken ct) =>
        Active.GenerateAsync(request, ct);

    private ISuggestionEngine Active => _useEndpoint ? _endpoint : _local;
}
