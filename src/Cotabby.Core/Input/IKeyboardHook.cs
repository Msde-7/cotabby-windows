namespace Cotabby.Core.Input;

/// <summary>
/// Global keyboard observer. Events are raised on the hook thread, so handlers must return
/// quickly and move real work elsewhere.
/// </summary>
public interface IKeyboardHook : IDisposable
{
    /// <summary>
    /// Raised for every keyboard event, before the host app sees it. Setting
    /// <see cref="KeyEventArgs.Suppress"/> swallows the event, which is how the accept key is
    /// kept from the host.
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
    /// Set by a handler to consume the event before it reaches the host application. Later
    /// handlers see it set and should treat the event as already consumed.
    /// </summary>
    public bool Suppress { get; set; }
}
