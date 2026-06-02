namespace Cotabby.Core.Suggestions;

/// <summary>
/// Owns the debounce timer and cancellation token for in-flight generation
/// work. The coordinator submits a new request via <see cref="Submit"/>; if a
/// previous request is still running it is cancelled first.
/// </summary>
/// <remarks>
/// Mirrors the macOS port's <c>SuggestionWorkController</c>. Kept in Core
/// because the logic is platform-agnostic — only the wall-clock source matters,
/// and that's injected for testability.
/// </remarks>
public sealed class SuggestionWorkController : IAsyncDisposable
{
    private readonly TimeSpan _debounce;
    private readonly object _gate = new();

    private CancellationTokenSource? _inFlight;
    private Task? _pendingTask;
    private bool _disposed;

    public SuggestionWorkController(TimeSpan debounce)
    {
        _debounce = debounce;
    }

    /// <summary>
    /// Schedule <paramref name="work"/> to run after the debounce window.
    /// Cancels any previously scheduled work. The returned task completes when
    /// the work either finishes, is cancelled, or throws.
    /// </summary>
    public Task Submit(Func<CancellationToken, Task> work, CancellationToken external = default)
    {
        if (_disposed) return Task.CompletedTask;

        CancellationTokenSource? prior;
        CancellationTokenSource cts;
        lock (_gate)
        {
            prior = _inFlight;
            cts = external == default
                ? new CancellationTokenSource()
                : CancellationTokenSource.CreateLinkedTokenSource(external);
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
            await Task.Delay(_debounce, cts.Token).ConfigureAwait(false);
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
