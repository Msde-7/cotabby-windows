namespace Cotabby.Core.Suggestions;

/// <summary>
/// Owns the debounce timer and the cancellation token for generation work. Submitting a new
/// request cancels the one before it.
/// </summary>
public sealed class SuggestionWorkController : IAsyncDisposable
{
    private readonly object _gate = new();

    private CancellationTokenSource? _inFlight;
    private Task? _pendingTask;
    private bool _disposed;

    public SuggestionWorkController(TimeSpan debounce) => Debounce = debounce;

    /// <summary>Quiet time after the last submit before work starts.</summary>
    public TimeSpan Debounce { get; set; }

    /// <summary>
    /// Schedules <paramref name="work"/> to run after the debounce window and cancels the work
    /// scheduled before it. The returned task completes when the work finishes, is cancelled
    /// or throws.
    /// </summary>
    public Task Submit(Func<CancellationToken, Task> work)
    {
        if (_disposed) return Task.CompletedTask;

        CancellationTokenSource? prior;
        CancellationTokenSource cts;
        lock (_gate)
        {
            prior = _inFlight;
            cts = new CancellationTokenSource();
            _inFlight = cts;
        }

        prior?.Cancel();
        prior?.Dispose();

        var task = RunAsync(work, cts);
        lock (_gate) { _pendingTask = task; }
        return task;
    }

    private async Task RunAsync(Func<CancellationToken, Task> work, CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(Debounce, cts.Token).ConfigureAwait(false);
            await work(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* expected */ }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_inFlight, cts))
                {
                    _inFlight = null;
                }
            }
            cts.Dispose();
        }
    }

    /// <summary>Cancel any pending or in-flight work without scheduling a new task.</summary>
    public void Cancel()
    {
        CancellationTokenSource? prior;
        lock (_gate)
        {
            prior = _inFlight;
            _inFlight = null;
        }
        prior?.Cancel();
        prior?.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        Cancel();
        Task? task;
        lock (_gate) { task = _pendingTask; }
        if (task is not null)
        {
            try { await task.ConfigureAwait(false); }
            catch { /* already logged at submit site */ }
        }
    }
}
