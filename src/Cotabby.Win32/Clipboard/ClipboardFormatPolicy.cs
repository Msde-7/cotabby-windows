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
/// Registered formats the policy treats specially. Registered ids differ from one session to the
/// next, so the caller looks them up by name.
/// </summary>
/// <param name="Pictures">Registered picture formats, such as PNG.</param>
/// <param name="Excluded">Registered formats never saved, such as OLE's link to the copying app's data object.</param>
public sealed record ClipboardRegisteredFormats(IReadOnlySet<uint> Pictures, IReadOnlySet<uint> Excluded)
{
    public static ClipboardRegisteredFormats None { get; } = new(new HashSet<uint>(), new HashSet<uint>());
}

/// <summary>
/// Chooses which of the formats on the clipboard are saved before a paste and put back after it.
/// </summary>
/// <remarks>
/// Content only its own app understands, such as a spreadsheet range with its formulas, a drawing
/// object or an image layer, is kept so it survives the paste. Formats Windows makes from another
/// one are left out and made again after the restore.
/// <para>
/// Saving a format makes its app render it, which for a large range or picture can take seconds
/// and a lot of memory. A picture copied along with text is a rendering of that text, as a
/// spreadsheet range or a document selection is, so it is left out and the app's own formats
/// carry the content. A picture copied on its own is kept.
/// </para>
/// <para>
/// A format whose data is not a memory block Cotabby can copy, such as a private handle or an
/// owner-drawn display format, is left out, as it is for any app the content is pasted into. So
/// is OLE's link to the copying app's data object, which the paste ends: put back, it would point
/// OLE apps at an object that is gone.
/// </para>
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

    /// <summary>Registered picture formats left out when text was copied too.</summary>
    public static IReadOnlyList<string> RegisteredPictureNames { get; } = ["PNG", "JFIF", "GIF", "image/png"];

    /// <summary>OLE's link to the copying app's data object and the format list that goes with it.</summary>
    public static IReadOnlyList<string> ExcludedRegisteredNames { get; } = ["DataObject", "Ole Private Data"];

    /// <summary>Standard formats that hold a memory block: SYLK, DIF, TIFF, pen data, RIFF, wave, file drop and locale.</summary>
    private static readonly HashSet<uint> MemoryBlockFormats = [4, 5, 6, 10, 11, 12, 15, 16];

    /// <summary>The formats to save, in the order to put them back.</summary>
    /// <param name="available">The formats the clipboard holds now, in the order it lists them.</param>
    /// <param name="registered">Registered formats to leave out or treat as pictures.</param>
    public static IReadOnlyList<ClipboardSaveStep> Plan(IReadOnlyList<uint> available, ClipboardRegisteredFormats? registered = null)
    {
        registered ??= ClipboardRegisteredFormats.None;
        bool hasUnicodeText = available.Contains(CF_UNICODETEXT);
        bool hasText = hasUnicodeText || available.Contains(CF_TEXT) || available.Contains(CF_OEMTEXT);
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
                case CF_DIB or CF_DIBV5 or CF_BITMAP or CF_PALETTE or CF_METAFILEPICT or CF_ENHMETAFILE when hasText:
                    break;
                case CF_DIB or CF_DIBV5:
                    if (format == bitmap) steps.Add(new(format, ClipboardSaveKind.Bytes));
                    break;
                // The bitmap handle and its palette are made again from the device-independent
                // bitmap, which Windows offers whenever there is a bitmap handle.
                case CF_BITMAP or CF_PALETTE:
                    break;
                // The old metafile and the enhanced one are made from each other, and the old one
                // holds a handle inside it.
                case CF_METAFILEPICT:
                    break;
                case CF_ENHMETAFILE:
                    steps.Add(new(format, ClipboardSaveKind.EnhancedMetafile));
                    break;
                case var _ when registered.Excluded.Contains(format):
                case var _ when hasText && registered.Pictures.Contains(format):
                    break;
                case >= FirstRegisteredFormat:
                case var _ when MemoryBlockFormats.Contains(format):
                    steps.Add(new(format, ClipboardSaveKind.Bytes));
                    break;
                // Private, GDI object and owner display formats hold handles only their owner understands.
                default:
                    break;
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
