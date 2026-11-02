using System.Diagnostics;

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

    /// <summary>Receives how long each finished request took, in milliseconds, on the generating thread.</summary>
    public Action<int>? Completed { get; set; }

    public async Task<string> GenerateAsync(SuggestionRequest request, CancellationToken ct, Action<string>? onPartial = null)
    {
        // The request was built from a settings snapshot, and the engine can change in between.
        // Typing history must never leave this PC, so a request that carries it is dropped.
        if (_useEndpoint && request.HistoryExamples.Count > 0) return string.Empty;
        var stopwatch = Stopwatch.StartNew();
        string text = await Active.GenerateAsync(request, ct, onPartial).ConfigureAwait(false);
        if (!ct.IsCancellationRequested) Completed?.Invoke((int)stopwatch.ElapsedMilliseconds);
        return text;
    }

    private ISuggestionEngine Active => _useEndpoint ? _endpoint : _local;
}
