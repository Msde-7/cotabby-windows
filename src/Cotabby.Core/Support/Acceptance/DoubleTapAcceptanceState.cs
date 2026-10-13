namespace Cotabby.Core.Support;

/// <summary>
/// State machine that recognizes a double press of the Accept Word key. The first press still
/// accepts one word immediately, and a second press of the same key inside <see cref="Window"/>
/// accepts everything that remains. A pair only counts on the same suggestion, inside the window,
/// and is consumed once so a triple press cannot promote twice. The owner supplies the clock.
/// </summary>
public sealed class DoubleTapAcceptanceState
{
    /// <summary>
    /// Identifies one suggestion across its word-by-word advancement. Accepting a word keeps both
    /// values stable, while a regenerated suggestion changes the generation.
    /// </summary>
    public readonly record struct SessionToken(ulong Generation, string FullText);

    /// <summary>
    /// Longest gap in seconds between the two presses that still counts as a double tap. Shorter
    /// than the system double-click interval so deliberate one-word-at-a-time accepting at a normal
    /// pace keeps accepting single words.
    /// </summary>
    public const double Window = 0.3;

    private (SessionToken Session, double Uptime)? _pendingPress;

    public bool HasPendingPress => _pendingPress is not null;

    /// <summary>Remembers a first press that accepted a word and left the suggestion with text remaining.</summary>
    public void RecordWordAccept(SessionToken session, double uptime) => _pendingPress = (session, uptime);

    /// <summary>
    /// Returns true when this press completes a double tap. Always clears the pending press, so a
    /// missed pair does not linger and a completed pair cannot be reused by a third press.
    /// </summary>
    public bool ConsumeDoubleTap(SessionToken session, double uptime)
    {
        var pending = _pendingPress;
        _pendingPress = null;
        if (pending is not { } press || press.Session != session) return false;
        double elapsed = uptime - press.Uptime;
        return elapsed >= 0 && elapsed <= Window;
    }

    /// <summary>Cancels a pending first press, for example when the user types between presses.</summary>
    public void Reset() => _pendingPress = null;
}
