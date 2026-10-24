namespace Cotabby.Core.Focus;

/// <summary>
/// Swallows a single failed read of the field that is still focused, so a host that briefly drops
/// its text or caret while it redraws does not hide and re-show the ghost text.
/// </summary>
/// <remarks>
/// Ported from the macOS <c>FocusCapabilityFlickerGate</c>. A failed read only counts when it names
/// the same element as the last good one. Focus on another element, or no element at all, passes
/// through at once. Not thread safe; the caller serializes access.
/// </remarks>
public sealed class FocusFlickerGate
{
    /// <summary>Failed reads in a row on the same field before the loss is passed on.</summary>
    public const int RequiredConsecutiveFailedReads = 2;

    public enum Decision
    {
        /// <summary>Use this read as it is.</summary>
        Apply,

        /// <summary>Treat the read as a flicker and keep the last good snapshot.</summary>
        Suppress,
    }

    private string? _lastFieldId;
    private int _failedReads;

    /// <summary>Feeds one read through the gate.</summary>
    /// <param name="elementId">Identity of the focused element, or null when none could be read.</param>
    /// <param name="captured">True when the read produced a usable field snapshot.</param>
    public Decision Evaluate(string? elementId, bool captured)
    {
        if (captured)
        {
            _lastFieldId = string.IsNullOrEmpty(elementId) ? null : elementId;
            _failedReads = 0;
            return Decision.Apply;
        }

        if (_lastFieldId is null || string.IsNullOrEmpty(elementId) || elementId != _lastFieldId)
        {
            Reset();
            return Decision.Apply;
        }

        _failedReads++;
        if (_failedReads >= RequiredConsecutiveFailedReads)
        {
            Reset();
            return Decision.Apply;
        }
        return Decision.Suppress;
    }

    public void Reset()
    {
        _lastFieldId = null;
        _failedReads = 0;
    }
}
