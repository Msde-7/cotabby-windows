namespace Cotabby.Core.Input;

/// <summary>
/// Global keyboard observer. Implementations run a low-level hook on a dedicated
/// thread; events are marshalled to the synchronization context the consumer
/// chooses (typically the UI thread for coordinators).
/// </summary>
public interface IKeyboardHook : IDisposable
{
    /// <summary>
    /// Raised for every keyboard event, before the host app sees it.
    /// Setting <see cref="KeyEventArgs.Suppress"/> to <c>true</c> swallows the
    /// event — used to eat the <c>Tab</c> that accepts a suggestion.
    /// </summary>
    event EventHandler<KeyEventArgs>? KeyEvent;

    /// <summary>Install the global hook. Idempotent.</summary>
    void Start();

    /// <summary>Uninstall the global hook. Idempotent.</summary>
    void Stop();
}

public sealed class KeyEventArgs : EventArgs
{
    public KeyEventArgs(KeyboardEvent evt) => Event = evt;

    public KeyboardEvent Event { get; }

    /// <summary>
    /// Set to <c>true</c> by a handler to consume the event before it reaches the
    /// host application. Only the first handler that sets it wins; subsequent
    /// handlers see <c>true</c> and should treat the event as already consumed.
    /// </summary>
    public bool Suppress { get; set; }
}
