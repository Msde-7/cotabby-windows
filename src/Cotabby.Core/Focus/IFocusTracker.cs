namespace Cotabby.Core.Focus;

/// <summary>Observes which editable field holds focus.</summary>
public interface IFocusTracker : IDisposable
{
    /// <summary>The most recent snapshot, or null if focus is not on a supported field.</summary>
    FocusedField? Current { get; }

    /// <summary>
    /// Raised when focus moves. Null means focus left every supported field, and listeners
    /// should end their active session.
    /// </summary>
    event EventHandler<FocusedField?>? FocusChanged;

    /// <summary>
    /// Reads the focused field again and returns the fresh snapshot. Used after a keystroke to
    /// see the new text and caret without waiting for a focus event.
    /// </summary>
    FocusedField? Refresh();

    /// <summary>Start watching. Idempotent.</summary>
    void Start();

    /// <summary>Stop watching. Idempotent.</summary>
    void Stop();
}
