namespace Cotabby.Core.Suggestions;

/// <summary>Running counters and recent latencies for the performance pane and the tray word count.</summary>
public sealed class SuggestionMetrics
{
    private const int MaxLatencySamples = 120;

    private readonly object _gate = new();
    private readonly Queue<int> _latenciesMs = new();

    public long SuggestionsShown { get; private set; }
    public long SuggestionsAccepted { get; private set; }
    public long WordsAccepted { get; private set; }

    public event Action? Changed;

    public IReadOnlyList<int> LatenciesMs
    {
        get { lock (_gate) return _latenciesMs.ToArray(); }
    }

    public void RecordShown(int latencyMs)
    {
        lock (_gate)
        {
            SuggestionsShown++;
            _latenciesMs.Enqueue(latencyMs);
            if (_latenciesMs.Count > MaxLatencySamples) _latenciesMs.Dequeue();
        }
        Changed?.Invoke();
    }

    public void RecordAccepted(string chunk, bool firstChunkOfSuggestion)
    {
        lock (_gate)
        {
            if (firstChunkOfSuggestion) SuggestionsAccepted++;
            WordsAccepted += AcceptanceChunker.AcceptedWordCount(chunk);
        }
        Changed?.Invoke();
    }

    /// <summary>Seeds the lifetime word count from persisted settings.</summary>
    public void Restore(long wordsAccepted)
    {
        lock (_gate) WordsAccepted = wordsAccepted;
    }
}
