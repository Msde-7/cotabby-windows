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

    /// <summary>
    /// Holds back every key the user types from now on, unseen by handlers and the host, until the
    /// returned hold and any other hold are released. The held keys are then sent again in the
    /// order they were typed and reach handlers and the host like fresh ones.
    /// </summary>
    /// <remarks>
    /// A handler calls this on the hook thread when it swallows a key whose effect it carries out
    /// later, such as the accept key: keys typed after it then land after its edit. That swallowed
    /// key belongs to the hold, so its release is not sent to the host and
    /// <see cref="IKeyHold.GiveBackKey"/> can hand it over after all. Called anywhere else, the hold
    /// has no key of its own. A hold that is not released within about a second lets the keys go.
    /// </remarks>
    IKeyHold BeginHold();
}

/// <summary>One hold on typed keys, from <see cref="IKeyboardHook.BeginHold"/>. Safe to use from any thread.</summary>
public interface IKeyHold
{
    /// <summary>
    /// Sends the key this hold swallowed to the host after all, ahead of the keys held behind it,
    /// as when an accept turns out to have nothing to insert. Call it before <see cref="Release"/>.
    /// Does nothing a second time or for a hold with no key of its own.
    /// </summary>
    void GiveBackKey();

    /// <summary>Ends the hold. Idempotent. The held keys are sent once no other hold remains.</summary>
    void Release();
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
