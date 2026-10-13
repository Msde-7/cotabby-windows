namespace Cotabby.Core.Input;

/// <summary>
/// A normalized keyboard event from <see cref="IKeyboardHook"/>. It carries only what the
/// coordinator and the inline features need to route a key.
/// </summary>
public sealed record KeyboardEvent
{
    /// <summary>The kind of key pressed.</summary>
    public required KeyKind Kind { get; init; }

    /// <summary>The printable character when <see cref="Kind"/> is <see cref="KeyKind.Character"/>, and '\0' otherwise.</summary>
    public required char Character { get; init; }

    /// <summary>True on key down, false on key up.</summary>
    public required bool IsKeyDown { get; init; }

    /// <summary>Windows virtual key code, or 0 for synthetic events that have none.</summary>
    public int VirtualKey { get; init; }

    /// <summary>Modifiers held while the key was pressed.</summary>
    public KeyModifiers Modifiers { get; init; }

    /// <summary>True when Ctrl, Alt or Win is held, which makes the key a shortcut and not typing.</summary>
    public bool HasNonShiftModifier => (Modifiers & ~KeyModifiers.Shift) != 0;

    /// <summary>True when the system generated this key down because the key is held.</summary>
    public bool IsRepeat { get; init; }
}

[Flags]
public enum KeyModifiers
{
    None = 0,
    Control = 1,
    Alt = 2,
    Shift = 4,
    Win = 8,
}

public enum KeyKind
{
    /// <summary>A printable character. <see cref="KeyboardEvent.Character"/> is valid.</summary>
    Character,

    /// <summary>The Tab key.</summary>
    Tab,

    /// <summary>Escape. Cancels the active suggestion.</summary>
    Escape,

    /// <summary>Enter. Commits the line and cancels the active suggestion.</summary>
    Enter,

    /// <summary>Backspace. Invalidates the active suggestion.</summary>
    Backspace,

    /// <summary>Delete. Invalidates the active suggestion.</summary>
    Delete,

    /// <summary>Arrow and navigation keys. Moving the caret invalidates the active suggestion.</summary>
    Arrow,

    /// <summary>A key pressed with Ctrl, Alt or Win, such as paste or undo. It can change the text in any way.</summary>
    Shortcut,

    /// <summary>Any other key, including a modifier on its own.</summary>
    Other,
}
