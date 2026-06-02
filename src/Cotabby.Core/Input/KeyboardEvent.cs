namespace Cotabby.Core.Input;

/// <summary>
/// A normalized keyboard event flowing out of <see cref="IKeyboardHook"/>. Only
/// carries what the coordinator actually needs to make routing decisions — the
/// raw scancode / virtual key is intentionally stripped to keep Core platform-free.
/// </summary>
public sealed record KeyboardEvent
{
    /// <summary>The kind of key pressed (printable / Tab / Escape / Arrow / …).</summary>
    public required KeyKind Kind { get; init; }

    /// <summary>The printable character if <see cref="Kind"/> is <see cref="KeyKind.Character"/>; otherwise '\0'.</summary>
    public required char Character { get; init; }

    /// <summary>True if any modifier (Ctrl, Alt, Win) is held — suggestion triggering should be suppressed.</summary>
    public required bool HasNonShiftModifier { get; init; }

    /// <summary>True on key down, false on key up.</summary>
    public required bool IsKeyDown { get; init; }
}

public enum KeyKind
{
    /// <summary>A printable character. <see cref="KeyboardEvent.Character"/> is valid.</summary>
    Character,

    /// <summary>The Tab key — used to accept suggestions.</summary>
    Tab,

    /// <summary>Escape — cancels the active suggestion.</summary>
    Escape,

    /// <summary>Enter / Return — commits the line and cancels active suggestion.</summary>
    Enter,

    /// <summary>Backspace — invalidates / shrinks the active suggestion.</summary>
    Backspace,

    /// <summary>Delete — invalidates the active suggestion.</summary>
    Delete,

    /// <summary>Arrow keys — moving the caret invalidates the active suggestion.</summary>
    Arrow,

    /// <summary>Any other non-printing key (Shift, Ctrl alone, Function keys, …).</summary>
    Other,
}
