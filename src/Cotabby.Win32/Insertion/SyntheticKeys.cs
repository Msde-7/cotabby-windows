namespace Cotabby.Win32.Insertion;

/// <summary>
/// One synthesized key event. A zero <see cref="VirtualKey"/> means <see cref="Character"/> is
/// sent as a Unicode keystroke.
/// </summary>
public readonly record struct SyntheticKey(ushort VirtualKey, char Character, bool KeyUp)
{
    public static SyntheticKey Down(ushort virtualKey) => new(virtualKey, '\0', KeyUp: false);
    public static SyntheticKey Up(ushort virtualKey) => new(virtualKey, '\0', KeyUp: true);
}

/// <summary>
/// Builds the key events of one SendInput batch. Every batch first releases the modifiers the
/// user still holds from the accept shortcut. With Ctrl down a Backspace would delete a whole
/// word and typed characters would run as shortcuts.
/// </summary>
public static class SyntheticKeys
{
    /// <summary>Left and right Shift, Ctrl and Alt, then both Win keys.</summary>
    public static readonly IReadOnlyList<ushort> Modifiers = [0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5, 0x5B, 0x5C];

    /// <summary>
    /// Longest text typed as keystrokes. Long bursts drop characters in many hosts, and a word
    /// accept is almost always shorter.
    /// </summary>
    public const int MaxTypedChars = 24;

    private const ushort VkBackspace = 0x08;
    private const ushort VkControl = 0x11;
    private const ushort VkV = 0x56;
    private const ushort VkLeftAlt = 0xA4;
    private const ushort VkRightAlt = 0xA5;
    private const ushort VkLeftWin = 0x5B;
    private const ushort VkRightWin = 0x5C;
    private const ushort VkUnassigned = 0xE8;

    /// <summary>True for short text with no line break, which hosts take as keystrokes.</summary>
    public static bool CanType(string text) =>
        text.Length <= MaxTypedChars && text.AsSpan().IndexOfAny("\n\r\v\f\u0085\u2028\u2029") < 0;

    /// <summary>Each UTF-16 unit as a Unicode key press, so surrogate pairs arrive intact.</summary>
    public static List<SyntheticKey> Text(IEnumerable<ushort> heldModifiers, string text)
    {
        var keys = Release(heldModifiers);
        foreach (char c in text)
        {
            keys.Add(new(0, c, KeyUp: false));
            keys.Add(new(0, c, KeyUp: true));
        }
        return keys;
    }

    public static List<SyntheticKey> Backspaces(IEnumerable<ushort> heldModifiers, int count)
    {
        var keys = Release(heldModifiers);
        for (int i = 0; i < count; i++)
        {
            keys.Add(SyntheticKey.Down(VkBackspace));
            keys.Add(SyntheticKey.Up(VkBackspace));
        }
        return keys;
    }

    /// <summary>Ctrl+V as virtual keys, because a shortcut is not a character.</summary>
    public static List<SyntheticKey> Paste(IEnumerable<ushort> heldModifiers)
    {
        var keys = Release(heldModifiers);
        keys.Add(SyntheticKey.Down(VkControl));
        keys.Add(SyntheticKey.Down(VkV));
        keys.Add(SyntheticKey.Up(VkV));
        keys.Add(SyntheticKey.Up(VkControl));
        return keys;
    }

    private static List<SyntheticKey> Release(IEnumerable<ushort> heldModifiers)
    {
        var held = heldModifiers.ToList();
        var keys = new List<SyntheticKey>();
        // Alt or Win released with no key in between opens the menu bar or the Start menu, so a
        // key that does nothing is pressed first.
        if (held.Any(key => key is VkLeftAlt or VkRightAlt or VkLeftWin or VkRightWin))
        {
            keys.Add(SyntheticKey.Down(VkUnassigned));
            keys.Add(SyntheticKey.Up(VkUnassigned));
        }
        keys.AddRange(held.Select(SyntheticKey.Up));
        return keys;
    }
}
