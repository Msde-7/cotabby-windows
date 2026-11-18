namespace Cotabby.Win32.Input;

/// <summary>Turns what the keyboard layout says about a key into the character the hook reports.</summary>
public static class KeyTranslation
{
    /// <summary>
    /// Reported for a typed character Cotabby cannot know. It matches no suggestion and no text, so
    /// the coordinator reads the field instead of trusting the key.
    /// </summary>
    public const char Unknown = '\uFFFD';

    /// <summary>
    /// The character of a key typed through VK_PACKET, which carries one UTF-16 unit. A surrogate
    /// pair arrives as two keys: the first reports nothing and the second an unknown character.
    /// </summary>
    public static char FromPacket(char unit) =>
        char.IsHighSurrogate(unit) ? '\0' : char.IsLowSurrogate(unit) ? Unknown : unit;

    /// <summary>
    /// The character of a key the layout translated. A dead key reports nothing, and the next
    /// character is unknown, since the host may show it accented or after the accent.
    /// </summary>
    /// <param name="count">What ToUnicodeEx returned: negative for a dead key, else the characters it made.</param>
    /// <param name="first">The first character it made.</param>
    /// <param name="afterDeadKey">Whether a dead key is waiting for the next character.</param>
    public static char FromLayout(int count, char first, ref bool afterDeadKey)
    {
        if (count < 0)
        {
            afterDeadKey = true;
            return '\0';
        }
        if (count == 0) return '\0';
        if (!afterDeadKey) return first;
        afterDeadKey = false;
        return Unknown;
    }

    /// <summary>
    /// Records a key down the hook does not translate. Backspace, Tab, Enter and Escape make a
    /// control character, which uses up a dead key in the host as any character does, so the letter
    /// after them is the plain letter. Arrows, chords and other keys that make no character are left
    /// alone: the host may still hold the dead key, and an unknown character only costs a field read.
    /// </summary>
    public static void Untranslated(int virtualKey, ref bool afterDeadKey)
    {
        if (virtualKey is 0x08 or 0x09 or 0x0D or 0x1B) afterDeadKey = false;
    }

    /// <summary>
    /// Records a key down once the handlers have had it. Only a key the host receives uses up its
    /// dead key: a swallowed accept Tab or a captured Escape never reached it, so the host still
    /// holds the dead key and the letter after it is still unknown. A key held behind an edit
    /// comes here when it is sent again, which is when the host gets it.
    /// </summary>
    public static void Handled(int virtualKey, bool suppressed, ref bool afterDeadKey)
    {
        if (!suppressed) Untranslated(virtualKey, ref afterDeadKey);
    }
}
