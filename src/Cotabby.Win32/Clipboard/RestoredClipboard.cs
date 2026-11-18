namespace Cotabby.Win32.Clipboard;

/// <summary>
/// Content Cotabby put back on the clipboard after a paste, by the sequence number it got, and
/// whether it was private before Cotabby saved it.
/// </summary>
/// <remarks>
/// Content Cotabby puts back carries its own markers that keep it out of clipboard history, and
/// those markers read as private. While the clipboard still holds what was put back, its privacy
/// is what it was before the paste, not what the markers say.
/// </remarks>
public readonly record struct RestoredClipboard(uint Sequence, bool IsPrivate)
{
    /// <summary>Whether the clipboard at <paramref name="now"/> holds private content.</summary>
    /// <param name="markersSayPrivate">What the privacy markers on the clipboard say.</param>
    public static bool IsPrivateAt(RestoredClipboard? last, uint now, bool markersSayPrivate) =>
        last is { } restored && restored.Sequence == now ? restored.IsPrivate : markersSayPrivate;

    /// <summary>
    /// The restore to remember after a paste: the one it made, or else the one before while the
    /// clipboard still holds it, as when the paste never got to write to the clipboard.
    /// </summary>
    public static RestoredClipboard? After(RestoredClipboard? last, RestoredClipboard? latest, uint now) =>
        latest ?? (last is { } restored && restored.Sequence == now ? restored : null);
}
