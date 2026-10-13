namespace Cotabby.Core.Input;

/// <summary>A classified key. <see cref="Modifiers"/> are the ones a consumer should see.</summary>
public readonly record struct ClassifiedKey(KeyKind Kind, char Character, KeyModifiers Modifiers);

/// <summary>Turns a virtual key and the held modifiers into the kind of key the consumers route on.</summary>
public static class KeyClassifier
{
    private const KeyModifiers AltGr = KeyModifiers.Control | KeyModifiers.Alt;
    private const KeyModifiers Chord = AltGr | KeyModifiers.Win;

    /// <param name="translate">
    /// Returns the character the active layout produces for the key, or '\0' for none. It is only
    /// called on key down, and never for Ctrl, Alt or Win chords other than AltGr.
    /// </param>
    public static ClassifiedKey Classify(int virtualKey, KeyModifiers held, bool isKeyDown, Func<char> translate)
    {
        if (NamedKind(virtualKey) is { } named) return new(named, '\0', held);

        // AltGr reports as Ctrl+Alt, and only the layout knows whether it makes a character.
        var chord = held & Chord;
        if (isKeyDown && (chord == KeyModifiers.None || chord == AltGr))
        {
            char c = translate();
            if (c != '\0' && !char.IsControl(c)) return new(KeyKind.Character, c, held & ~AltGr);
        }

        return new(chord == KeyModifiers.None ? KeyKind.Other : KeyKind.Shortcut, '\0', held);
    }

    /// <summary>The kind of a key that keeps it whatever modifiers are held, or null for every other key.</summary>
    private static KeyKind? NamedKind(int virtualKey) => virtualKey switch
    {
        0x09 => KeyKind.Tab,
        0x1B => KeyKind.Escape,
        0x0D => KeyKind.Enter,
        0x08 => KeyKind.Backspace,
        0x2E => KeyKind.Delete,
        // Page up, page down, end, home and the four arrows.
        >= 0x21 and <= 0x28 => KeyKind.Arrow,
        // Shift, Ctrl, Alt and Win, with the left and right codes a low-level hook reports.
        0x10 or 0x11 or 0x12 or 0x5B or 0x5C or (>= 0xA0 and <= 0xA5) => KeyKind.Other,
        _ => null,
    };
}
