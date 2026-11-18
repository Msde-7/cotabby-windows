namespace Cotabby.Win32.Clipboard;

/// <summary>How one saved clipboard format is copied out and put back.</summary>
public enum ClipboardSaveKind
{
    /// <summary>A global memory block, copied byte for byte.</summary>
    Bytes,

    /// <summary>An enhanced metafile handle, copied through its bits.</summary>
    EnhancedMetafile,
}

public readonly record struct ClipboardSaveStep(uint Format, ClipboardSaveKind Kind);

/// <summary>
/// Chooses which of the formats on the clipboard are saved before a paste and put back after it.
/// </summary>
/// <remarks>
/// Every format the clipboard offers is kept, so content only its own app understands, such as a
/// spreadsheet range with its formulas, a drawing object or an image layer, survives the paste.
/// Formats Windows makes from another one are left out and made again after the restore. A
/// clipboard holding a format whose data is not a memory block Cotabby can copy, such as a private
/// handle or an owner-drawn display format, is not saved at all, and then nothing may be pasted
/// through it.
/// </remarks>
public static class ClipboardFormatPolicy
{
    public const uint CF_TEXT = 1;
    public const uint CF_BITMAP = 2;
    public const uint CF_METAFILEPICT = 3;
    public const uint CF_OEMTEXT = 7;
    public const uint CF_DIB = 8;
    public const uint CF_PALETTE = 9;
    public const uint CF_UNICODETEXT = 13;
    public const uint CF_ENHMETAFILE = 14;
    public const uint CF_DIBV5 = 17;

    /// <summary>The first id of formats an app registers by name, which Windows stores as memory blocks.</summary>
    public const uint FirstRegisteredFormat = 0xC000;

    /// <summary>Standard formats that hold a memory block: SYLK, DIF, TIFF, pen data, RIFF, wave, file drop and locale.</summary>
    private static readonly HashSet<uint> MemoryBlockFormats = [4, 5, 6, 10, 11, 12, 15, 16];

    /// <summary>
    /// The formats to save, in the order to put them back, or null when one of them cannot be
    /// saved faithfully.
    /// </summary>
    /// <param name="available">The formats the clipboard holds now, in the order it lists them.</param>
    public static IReadOnlyList<ClipboardSaveStep>? Plan(IReadOnlyList<uint> available)
    {
        bool hasUnicodeText = available.Contains(CF_UNICODETEXT);
        bool hasMetafile = available.Contains(CF_ENHMETAFILE);
        // A device-independent bitmap comes in two versions that Windows converts between. The
        // first one listed is the one the app put there.
        uint bitmap = available.FirstOrDefault(format => format is CF_DIB or CF_DIBV5);

        var steps = new List<ClipboardSaveStep>();
        foreach (uint format in available)
        {
            switch (format)
            {
                // Windows makes the 8-bit texts from the Unicode text when only the latter is put back.
                case CF_TEXT or CF_OEMTEXT when hasUnicodeText:
                    break;
                case CF_TEXT or CF_OEMTEXT or CF_UNICODETEXT:
                    steps.Add(new(format, ClipboardSaveKind.Bytes));
                    break;
                case CF_DIB or CF_DIBV5:
                    if (format == bitmap) steps.Add(new(format, ClipboardSaveKind.Bytes));
                    break;
                // The bitmap handle and its palette are made again from the device-independent bitmap.
                case CF_BITMAP or CF_PALETTE when bitmap != 0:
                    break;
                // The old metafile is made again from the enhanced one, and the other way round.
                case CF_METAFILEPICT when hasMetafile:
                    break;
                case CF_ENHMETAFILE:
                    steps.Add(new(format, ClipboardSaveKind.EnhancedMetafile));
                    break;
                case >= FirstRegisteredFormat:
                case var _ when MemoryBlockFormats.Contains(format):
                    steps.Add(new(format, ClipboardSaveKind.Bytes));
                    break;
                // Private, GDI object and owner display formats hold handles only their owner understands.
                default:
                    return null;
            }
        }
        return steps;
    }

    /// <summary>
    /// True when the app that copied the content asked clipboard tools to leave it alone, as password
    /// managers do for a copied secret.
    /// </summary>
    /// <param name="excludeFromMonitoring">The clipboard holds ExcludeClipboardContentFromMonitorProcessing.</param>
    /// <param name="viewerIgnore">The clipboard holds Clipboard Viewer Ignore.</param>
    /// <param name="canIncludeInHistory">The CanIncludeInClipboardHistory data, empty when it is not there.</param>
    public static bool IsPrivate(bool excludeFromMonitoring, bool viewerIgnore, ReadOnlySpan<byte> canIncludeInHistory) =>
        excludeFromMonitoring || viewerIgnore
        || (canIncludeInHistory.Length >= sizeof(uint) && BitConverter.ToUInt32(canIncludeInHistory) == 0);
}
