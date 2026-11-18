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

    /// <summary>Registered formats only their own app understands, other than HTML, such as a spreadsheet range or rich text.</summary>
    public IReadOnlySet<uint> RichApp { get; init; } = new HashSet<uint>();

    /// <summary>OLE's embedded or linked copy of the content and its descriptors.</summary>
    public IReadOnlySet<uint> Embedding { get; init; } = new HashSet<uint>();

    /// <summary>Registered formats that only mark the content, such as the clipboard history markers.</summary>
    public IReadOnlySet<uint> Markers { get; init; } = new HashSet<uint>();
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
/// and a lot of memory. A picture copied along with text and an app's own rich format, as a
/// spreadsheet range or a document selection is, is a rendering of that content, so it is left
/// out and the app's own formats carry the content. So is OLE's embedded copy of it. A picture
/// copied on its own or with plain text and HTML, as a browser's Copy Image does, is kept.
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
    public const uint CF_SYLK = 4;
    public const uint CF_DIF = 5;
    public const uint CF_OEMTEXT = 7;
    public const uint CF_DIB = 8;
    public const uint CF_PALETTE = 9;
    public const uint CF_UNICODETEXT = 13;
    public const uint CF_ENHMETAFILE = 14;
    public const uint CF_DIBV5 = 17;
    public const uint CF_LOCALE = 16;

    /// <summary>The first id of formats an app registers by name, which Windows stores as memory blocks.</summary>
    public const uint FirstRegisteredFormat = 0xC000;

    /// <summary>Registered picture formats, left out when an app's own rich format was copied too.</summary>
    public static IReadOnlyList<string> RegisteredPictureNames { get; } = ["PNG", "JFIF", "GIF", "image/png"];

    /// <summary>OLE's link to the copying app's data object and the format list that goes with it.</summary>
    public static IReadOnlyList<string> ExcludedRegisteredNames { get; } = ["DataObject", "Ole Private Data"];

    /// <summary>OLE's embedded or linked copy of the content and the descriptors that go with it.</summary>
    public static IReadOnlyList<string> EmbeddingNames { get; } =
        ["Embed Source", "Link Source", "Object Descriptor", "Link Source Descriptor"];

    /// <summary>
    /// Formats only their own app understands, other than HTML: Excel's ranges, rich text and OLE
    /// embeddings. A picture copied beside one and text is a rendering of the same content.
    /// </summary>
    public static IReadOnlyList<string> RichAppNames { get; } =
        ["Biff12", "Biff8", "Biff5", "XML Spreadsheet", "Rich Text Format", .. EmbeddingNames];

    /// <summary>Formats that mark the content for clipboard history and clipboard tools, and hold none of it.</summary>
    public static IReadOnlyList<string> MarkerNames { get; } =
    [
        "ExcludeClipboardContentFromMonitorProcessing", "CanIncludeInClipboardHistory", "CanUploadToCloudClipboard",
        "Clipboard Viewer Ignore",
    ];

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
        // A spreadsheet range or a document selection. Its pictures render slowly in Excel and Office.
        bool renderedFromText = hasText
            && available.Any(format => format is CF_SYLK or CF_DIF || registered.RichApp.Contains(format));

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
                case CF_DIB or CF_DIBV5 or CF_BITMAP or CF_PALETTE or CF_METAFILEPICT or CF_ENHMETAFILE when renderedFromText:
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
                case var _ when renderedFromText && registered.Pictures.Contains(format):
                // An embedded copy of copied text is rendered as expensively, and the text formats carry it.
                case var _ when hasText && registered.Embedding.Contains(format):
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
    /// True when what was saved can stand in for the clipboard, so it may be replaced. False when a
    /// text format came back empty, or when the clipboard held content and none of what was saved
    /// carries it, such as when every format came back empty because the copying app exited or a
    /// remote clipboard failed, or the content is in a private format: the restore would then put
    /// back nothing, and the user's copy would be lost.
    /// </summary>
    /// <param name="available">The formats the clipboard holds.</param>
    /// <param name="plan">The formats <see cref="Plan"/> chose to save.</param>
    /// <param name="saved">The formats saved with their data.</param>
    /// <param name="registered">Registered formats to treat specially.</param>
    public static bool CanSwap(
        IReadOnlyList<uint> available, IReadOnlyList<ClipboardSaveStep> plan, IReadOnlyCollection<uint> saved,
        ClipboardRegisteredFormats? registered = null)
    {
        registered ??= ClipboardRegisteredFormats.None;
        if (plan.Any(step => IsText(step.Format) && !saved.Contains(step.Format))) return false;
        bool hadContent = available.Any(format => format != CF_LOCALE && !registered.Markers.Contains(format));
        return !hadContent || saved.Any(format => CarriesContent(format, registered));
    }

    private static bool IsText(uint format) => format is CF_TEXT or CF_OEMTEXT or CF_UNICODETEXT;

    /// <summary>Text, a picture, a file list or an app's own format, as opposed to the locale or a marker.</summary>
    private static bool CarriesContent(uint format, ClipboardRegisteredFormats registered) => format switch
    {
        CF_LOCALE => false,
        _ when IsText(format) => true,
        CF_DIB or CF_DIBV5 or CF_ENHMETAFILE => true,
        >= FirstRegisteredFormat => !registered.Markers.Contains(format) && !registered.Excluded.Contains(format),
        _ => MemoryBlockFormats.Contains(format),
    };

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
