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
    /// see the new text and caret without waiting for a focus event. With
    /// <paramref name="readHostText"/> the snapshot also carries the host's font and caret line,
    /// which cost extra reads and are only needed to draw a suggestion.
    /// </summary>
    FocusedField? Refresh(bool readHostText = false);

    /// <summary>Start watching and raise <see cref="FocusChanged"/> with the field focused now. Idempotent.</summary>
    void Start();

    /// <summary>Stop watching. Idempotent.</summary>
    void Stop();
}
