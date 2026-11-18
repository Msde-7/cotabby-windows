namespace Cotabby.Win32.Insertion;

/// <summary>
/// One synthesized key event. A zero <see cref="VirtualKey"/> means <see cref="Character"/> is
/// sent as a Unicode keystroke. <see cref="Restores"/> marks the press of a modifier the user still
/// holds, which the keyboard hook drops if the user let go of it before the press arrived.
/// </summary>
public readonly record struct SyntheticKey(ushort VirtualKey, char Character, bool KeyUp, bool Restores = false)
{
    public static SyntheticKey Down(ushort virtualKey) => new(virtualKey, '\0', KeyUp: false);
    public static SyntheticKey Up(ushort virtualKey) => new(virtualKey, '\0', KeyUp: true);
    public static SyntheticKey Restore(ushort virtualKey) => new(virtualKey, '\0', KeyUp: false, Restores: true);
}

/// <summary>
/// Builds the key events of one SendInput batch. Every batch first releases the modifiers the
/// user still holds from the accept shortcut. With Ctrl down a Backspace would delete a whole
/// word and typed characters would run as shortcuts. The batch ends by pressing them again, so
/// the key the user types next, or the second press of a held shortcut, still carries them.
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
    public static bool CanType(string text) => text.Length <= MaxTypedChars && !HasLineBreak(text);

    /// <summary>True when the text holds any character a host may take as a line or paragraph break.</summary>
    public static bool HasLineBreak(string text) => text.AsSpan().IndexOfAny("\n\r\v\f\u0085\u2028\u2029") >= 0;

    /// <summary>Each UTF-16 unit as a Unicode key press, so surrogate pairs arrive intact.</summary>
    public static List<SyntheticKey> Text(IEnumerable<ushort> heldModifiers, string text)
    {
        var held = heldModifiers.ToList();
        var keys = Release(held);
        foreach (char c in text)
        {
            keys.Add(new(0, c, KeyUp: false));
            keys.Add(new(0, c, KeyUp: true));
        }
        PressAgain(keys, held);
        return keys;
    }

    public static List<SyntheticKey> Backspaces(IEnumerable<ushort> heldModifiers, int count)
    {
        var held = heldModifiers.ToList();
        var keys = Release(held);
        for (int i = 0; i < count; i++)
        {
            keys.Add(SyntheticKey.Down(VkBackspace));
            keys.Add(SyntheticKey.Up(VkBackspace));
        }
        PressAgain(keys, held);
        return keys;
    }

    /// <summary>
    /// A press of a key that does nothing. Sent while Alt or Win is still held after the key that
    /// went with it was swallowed, so the release that follows does not open the menu bar or the
    /// Start menu.
    /// </summary>
    public static List<SyntheticKey> MenuMask() => [SyntheticKey.Down(VkUnassigned), SyntheticKey.Up(VkUnassigned)];

    /// <summary>
    /// Cuts text into runs of at most <paramref name="maxChars"/> UTF-16 units, typed one burst
    /// at a time. A run never ends between the two halves of a surrogate pair.
    /// </summary>
    public static IEnumerable<string> Bursts(string text, int maxChars = MaxTypedChars)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxChars, 2);
        int start = 0;
        while (start < text.Length)
        {
            int length = Math.Min(maxChars, text.Length - start);
            if (start + length < text.Length && char.IsHighSurrogate(text[start + length - 1])) length--;
            yield return text.Substring(start, length);
            start += length;
        }
    }

    /// <summary>Ctrl+V as virtual keys, because a shortcut is not a character.</summary>
    public static List<SyntheticKey> Paste(IEnumerable<ushort> heldModifiers)
    {
        var held = heldModifiers.ToList();
        var keys = Release(held);
        keys.Add(SyntheticKey.Down(VkControl));
        keys.Add(SyntheticKey.Down(VkV));
        keys.Add(SyntheticKey.Up(VkV));
        keys.Add(SyntheticKey.Up(VkControl));
        PressAgain(keys, held);
        return keys;
    }

    private static List<SyntheticKey> Release(List<ushort> held)
    {
        var keys = new List<SyntheticKey>();
        // Alt or Win released with no key in between opens the menu bar or the Start menu, so a
        // key that does nothing is pressed first.
        if (HoldsAltOrWin(held)) keys.AddRange(MenuMask());
        keys.AddRange(held.Select(SyntheticKey.Up));
        return keys;
    }

    /// <summary>
    /// Presses the released modifiers again. Alt or Win pressed again is followed by the key that
    /// does nothing, for the same reason as on release: the user's own release comes next.
    /// </summary>
    private static void PressAgain(List<SyntheticKey> keys, List<ushort> held)
    {
        keys.AddRange(held.Select(SyntheticKey.Restore));
        if (HoldsAltOrWin(held)) keys.AddRange(MenuMask());
    }

    private static bool HoldsAltOrWin(List<ushort> held) =>
        held.Any(key => key is VkLeftAlt or VkRightAlt or VkLeftWin or VkRightWin);
}
