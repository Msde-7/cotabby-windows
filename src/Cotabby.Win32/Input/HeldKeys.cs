namespace Cotabby.Win32.Input;

/// <summary>
/// The keys the keyboard hook has seen go down and not yet up, used to tell auto-repeat from a
/// second press. Only the hook thread touches it.
/// </summary>
/// <remarks>
/// A release can be missed, for example when Win+L locks the PC and the keys come up on the secure
/// desktop, where the hook hears nothing. A key stuck as held would make every later press of it a
/// repeat, and the double-tap and the global toggle would stop working. Windows repeats only the
/// key pressed last, and at least once a second, so a press of a held key counts as a repeat only
/// when no other key went down since and it comes soon after the last one.
/// </remarks>
public sealed class HeldKeys
{
    /// <summary>Longer than the slowest keyboard repeat delay Windows offers, which is one second.</summary>
    private const uint MaxRepeatGapMs = 1500;

    private readonly HashSet<uint> _held = [];
    private uint _lastDown;
    private uint _lastDownTime;

    /// <summary>Records a key down and says whether it is an auto-repeat.</summary>
    /// <param name="time">The event's tick count in milliseconds, which wraps around.</param>
    public bool Down(uint virtualKey, uint time)
    {
        bool repeat = !_held.Add(virtualKey)
            && _lastDown == virtualKey
            && unchecked(time - _lastDownTime) <= MaxRepeatGapMs;
        _lastDown = virtualKey;
        _lastDownTime = time;
        return repeat;
    }

    public void Up(uint virtualKey) => _held.Remove(virtualKey);

    public bool IsHeld(uint virtualKey) => _held.Contains(virtualKey);

    public void Clear()
    {
        _held.Clear();
        _lastDown = 0;
    }
}
