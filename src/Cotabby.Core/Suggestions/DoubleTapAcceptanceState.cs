namespace Cotabby.Core.Suggestions;

/// <summary>
/// Recognizes a double press of the accept-word key. The first press still takes one word at
/// once, and a second press on the same suggestion inside <see cref="Window"/> takes the rest.
/// </summary>
/// <remarks>
/// A pair only counts on the same suggestion, inside the window and with time moving forward.
/// Each check consumes the pending press, so a third press cannot promote again.
/// </remarks>
public sealed class DoubleTapAcceptanceState
{
    /// <summary>
    /// Longest gap between the presses. Shorter than the usual double-click time, so pressing
    /// Tab word by word at a normal pace keeps taking single words.
    /// </summary>
    public static readonly TimeSpan Window = TimeSpan.FromMilliseconds(300);

    private (string RequestId, double Seconds)? _pending;

    public bool HasPendingPress => _pending is not null;

    /// <summary>Remembers a press that accepted a word and left text to accept.</summary>
    public void RecordWordAccept(string requestId, double seconds) => _pending = (requestId, seconds);

    /// <summary>True when this press completes a double tap. Always clears the pending press.</summary>
    public bool ConsumeDoubleTap(string requestId, double seconds)
    {
        var pending = _pending;
        _pending = null;
        if (pending is not { } press || press.RequestId != requestId) return false;
        double elapsed = seconds - press.Seconds;
        return elapsed >= 0 && elapsed <= Window.TotalSeconds;
    }

    /// <summary>Cancels a pending press, as when a key arrives between the two.</summary>
    public void Reset() => _pending = null;
}
