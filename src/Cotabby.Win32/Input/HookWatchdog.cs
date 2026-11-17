namespace Cotabby.Win32.Input;

/// <summary>
/// Decides from two running counts whether Windows has dropped the low-level keyboard hook.
/// </summary>
/// <remarks>
/// Windows removes a WH_KEYBOARD_LL hook without telling its owner when one callback runs past
/// LowLevelHooksTimeout. Raw keyboard input keeps arriving after that, and Windows calls the hook
/// before it delivers raw input, so keys seen through raw input while the hook saw none mean the
/// hook is gone. Mouse input cannot be told apart from typing through GetLastInputInfo, which is
/// why the check counts raw keyboard input instead.
/// <para>
/// The hook is also silent while an elevated window has focus, because Windows keeps the input of
/// a higher integrity process from a lower one's hooks. One reinstall is spent per silent stretch,
/// and the next only once the hook has been heard from again, so typing in an administrator
/// window does not reinstall the hook every second.
/// </para>
/// </remarks>
public sealed class HookWatchdog
{
    /// <summary>
    /// Fewest raw keys in one period, without a hook callback, that count as a dropped hook. One
    /// key's callback can land in the period before its raw input does.
    /// </summary>
    public const int MinimumUnheardKeys = 2;

    private long _lastCallbacks;
    private long _lastRawKeys;
    private bool _armed = true;

    /// <summary>
    /// Takes the counts at the end of one period. Returns true when the hook should be reinstalled.
    /// </summary>
    /// <param name="callbacks">Hook callbacks so far, of any kind.</param>
    /// <param name="rawKeys">Raw keyboard input messages so far.</param>
    public bool Check(long callbacks, long rawKeys)
    {
        long heard = callbacks - _lastCallbacks;
        long typed = rawKeys - _lastRawKeys;
        _lastCallbacks = callbacks;
        _lastRawKeys = rawKeys;

        if (heard > 0)
        {
            _armed = true;
            return false;
        }
        if (!_armed || typed < MinimumUnheardKeys) return false;
        _armed = false;
        return true;
    }
}
