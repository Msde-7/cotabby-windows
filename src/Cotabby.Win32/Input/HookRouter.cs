using Cotabby.Core.Input;

namespace Cotabby.Win32.Input;

/// <summary>Whose key an event is, read from the mark Cotabby puts on what it sends.</summary>
public enum InputMark
{
    /// <summary>Typed by the user, or injected by other software, which is a person typing through it.</summary>
    None,

    /// <summary>Cotabby's own edit.</summary>
    Input,

    /// <summary>A modifier Cotabby presses again after a batch, because the user still held it.</summary>
    Restore,

    /// <summary>A key the user typed while keys were held, sent again.</summary>
    Replay,
}

/// <summary>What the keyboard hook does with an event before its handlers run.</summary>
public enum HookStep
{
    /// <summary>Hand it to the next hook untouched, with no handler seeing it.</summary>
    Pass,

    /// <summary>Keep it from the host, with no handler seeing it.</summary>
    Suppress,

    /// <summary>Hand it to the handlers, which may swallow it.</summary>
    Raise,
}

/// <summary>
/// The keyboard hook's decisions that come before its handlers: Cotabby's own keys, keys held
/// behind an edit, and which keys are down. Only the hook thread calls it.
/// </summary>
public sealed class HookRouter(KeyHold hold)
{
    public KeyHold Hold { get; } = hold;

    /// <summary>Keys the handlers have seen go down and not yet up.</summary>
    public HeldKeys HeldKeys { get; } = new();

    /// <param name="key">The event.</param>
    /// <param name="mark">Its mark.</param>
    /// <param name="time">Its tick count, for telling auto-repeat from a second press.</param>
    /// <param name="isRepeat">For a key down handed to the handlers, whether it is an auto-repeat.</param>
    public HookStep Route(in RawKey key, InputMark mark, uint time, out bool isRepeat)
    {
        isRepeat = false;
        uint vk = (uint)key.VirtualKey;
        switch (mark)
        {
            case InputMark.Input:
                // Reacting to Cotabby's own keys would loop, and held keys must not wait for the
                // edit they are held behind.
                return HookStep.Pass;
            case InputMark.Restore:
                // A modifier pressed again after a batch is dropped when the user let go of it
                // before the press arrived, so it is not left down. Events reach the hook in order,
                // so the release has been seen by now. A release still held behind an edit has not:
                // the modifier is pressed again here and released when the release is sent again.
                return HeldKeys.IsHeld(vk) ? HookStep.Pass : HookStep.Suppress;
        }

        switch (Hold.Arrive(key, mark == InputMark.Replay))
        {
            case KeyRoute.Hold:
                return HookStep.Suppress;
            case KeyRoute.Drop:
                HeldKeys.Up(vk);
                return HookStep.Suppress;
        }

        if (key.IsKeyUp) HeldKeys.Up(vk);
        else isRepeat = HeldKeys.Down(vk, time);
        return HookStep.Raise;
    }
}
