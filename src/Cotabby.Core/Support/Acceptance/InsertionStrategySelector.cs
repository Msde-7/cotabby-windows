namespace Cotabby.Core.Support;

/// <summary>How an accepted suggestion is committed to the host app.</summary>
public enum InsertionStrategy
{
    /// <summary>Synthesize the text as keyboard input. The default, clipboard-free path.</summary>
    Keystroke,

    /// <summary>Place the text on the clipboard and synthesize a paste.</summary>
    Paste,
}

/// <summary>
/// Chooses between synthetic keystrokes and paste. Keystrokes are reliable and clipboard-free for
/// the common short, single-line completion, but some apps mishandle a long or multi-line synthetic
/// string, where pasting is steadier.
/// </summary>
public static class InsertionStrategySelector
{
    /// <summary>At or above this many characters a completion is a paste candidate.</summary>
    public const int PasteCharacterThreshold = 80;

    /// <summary>
    /// Keystroke whenever paste insertion is disabled. Otherwise multi-line or long chunks paste and
    /// the rest keystroke.
    /// </summary>
    public static InsertionStrategy Strategy(string chunk, bool pasteEnabled)
    {
        if (!pasteEnabled) return InsertionStrategy.Keystroke;
        foreach (char unit in chunk)
        {
            if (ScalarSets.IsNewline(unit)) return InsertionStrategy.Paste;
        }
        return GraphemeString.CountOf(chunk) >= PasteCharacterThreshold
            ? InsertionStrategy.Paste
            : InsertionStrategy.Keystroke;
    }
}
