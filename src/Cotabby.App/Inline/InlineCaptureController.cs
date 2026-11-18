using Cotabby.Core.Focus;
using Cotabby.Core.Input;
using Cotabby.Core.Insertion;
using Microsoft.Extensions.Logging;

namespace Cotabby.App.Inline;

/// <summary>
/// What the inline features that capture typed text, the emoji picker and slash macros, share:
/// the key and focus subscriptions, the commit key, reading the caret once a key has landed and
/// background work that logs its failures. Each feature keeps its own lock, since each guards
/// different state.
/// </summary>
public abstract class InlineCaptureController : IDisposable
{
    /// <summary>Time for the host to apply a key before the caret is read.</summary>
    private const int CaretSettleMs = 30;

    private readonly IKeyboardHook _hook;

    protected InlineCaptureController(IKeyboardHook hook, IFocusTracker focus, ITextInserter inserter, ILogger logger)
    {
        _hook = hook;
        Focus = focus;
        Inserter = inserter;
        Logger = logger;
    }

    protected IFocusTracker Focus { get; }

    protected ITextInserter Inserter { get; }

    protected ILogger Logger { get; }

    /// <summary>The key that commits in an app. It follows the accept-word shortcut.</summary>
    public Func<string?, KeyBinding> CommitKeyFor { get; set; } = _ => KeyBinding.Tab;

    /// <summary>The key as the capture sees it, with the focused app's commit key standing in for Tab.</summary>
    protected KeyboardEvent WithCommitKey(KeyboardEvent ev) =>
        InlineCommitKey.Normalize(ev, CommitKeyFor(Focus.Current?.ProcessName));

    /// <summary>Starts receiving keys and focus changes. Called last in the constructor, once the state they touch exists.</summary>
    protected void Subscribe()
    {
        _hook.KeyEvent += OnKeyEvent;
        Focus.FocusChanged += OnFocusChanged;
    }

    protected abstract void OnKeyEvent(object? sender, KeyEventArgs args);

    protected abstract void OnFocusChanged(object? sender, FocusedField? field);

    /// <summary>Releases what the feature holds, once it no longer receives keys or focus changes.</summary>
    protected abstract void OnDispose();

    /// <summary>Reads the field after the host has had time to apply the key just seen. Call it off the hook thread.</summary>
    protected async Task<FocusedField?> ReadSettledFieldAsync()
    {
        await Task.Delay(CaretSettleMs).ConfigureAwait(false);
        return Focus.Refresh();
    }

    /// <summary>Deletes the typed run before the caret and inserts <paramref name="text"/> in its place.</summary>
    protected async Task ReplaceTypedAsync(FocusedField field, int deleteCount, string text, string feature)
    {
        if (await Inserter.ReplaceAsync(field, deleteCount, text, CancellationToken.None).ConfigureAwait(false))
        {
            Logger.LogInformation("{Feature} committed (replaced {Count} chars).", feature, deleteCount);
        }
        else
        {
            Logger.LogInformation("{Feature} was not committed: the edit could not be applied.", feature);
        }
    }

    /// <summary>
    /// Runs work on the thread pool, so it never runs inside the hook callback, and logs a failure
    /// that would otherwise go unobserved.
    /// </summary>
    protected Task RunInBackground(string operation, Func<Task> work) => Task.Run(async () =>
    {
        try
        {
            await work().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "{Operation} failed.", operation);
        }
    });

    /// <inheritdoc cref="RunInBackground(string, Func{Task})"/>
    protected Task RunInBackground(string operation, Action work) => RunInBackground(operation, () =>
    {
        work();
        return Task.CompletedTask;
    });

    public void Dispose()
    {
        _hook.KeyEvent -= OnKeyEvent;
        Focus.FocusChanged -= OnFocusChanged;
        OnDispose();
    }
}
