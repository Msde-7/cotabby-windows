namespace Cotabby.Core.Focus;

/// <summary>
/// Observes which editable field currently holds focus and surfaces changes
/// as events. Implementations may use polling, event subscription, or a hybrid.
/// </summary>
public interface IFocusTracker : IDisposable
{
    /// <summary>The most recent snapshot, or null if focus is not on a supported field.</summary>
    FocusedField? Current { get; }

    /// <summary>
    /// Raised when the focused field changes identity. <c>null</c> means "focus left
    /// any supported field" — coordinators should tear down active sessions on null.
    /// </summary>
    event EventHandler<FocusedField?>? FocusChanged;

    /// <summary>
    /// Re-resolves the current focused field immediately, returning the freshly
    /// captured snapshot. Use after a keystroke so the coordinator can read the
    /// new caret position / text without waiting for the next event.
    /// </summary>
    FocusedField? Refresh();

    /// <summary>Start watching. Idempotent.</summary>
    void Start();

    /// <summary>Stop watching. Idempotent.</summary>
    void Stop();
}
