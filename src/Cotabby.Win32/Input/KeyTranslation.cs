namespace Cotabby.Win32.Input;

/// <summary>Turns what the keyboard layout says about a key into the character the hook reports.</summary>
public static class KeyTranslation
{
    /// <summary>
    /// Reported for a typed character Cotabby cannot know. It matches no suggestion and no text, so
    /// the coordinator reads the field instead of trusting the key.
    /// </summary>
    public const char Unknown = '�';

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
}
