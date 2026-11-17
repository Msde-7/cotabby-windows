namespace Cotabby.Win32.Clipboard;

/// <summary>
/// Chooses which of the formats on the clipboard are saved before a paste and put back after it.
/// </summary>
/// <remarks>
/// Saving a format makes its owner render it, which for a large spreadsheet range or picture
/// can take a while and a lot of memory. Only formats people paste are kept: text, rich text,
/// HTML and copied files. A picture is kept only when no text was copied with it, since an app
/// that offers text alongside a picture (a spreadsheet range, a document selection) can rebuild
/// what matters from the text formats.
/// </remarks>
public static class ClipboardFormatPolicy
{
    public const string UnicodeText = "UnicodeText";
    public const string Text = "Text";
    public const string Html = "HTML Format";
    public const string Rtf = "Rich Text Format";
    public const string FileDrop = "FileDrop";
    public const string PreferredDropEffect = "Preferred DropEffect";
    public const string ShellIdList = "Shell IDList Array";
    public const string Dib = "DeviceIndependentBitmap";
    public const string Png = "PNG";

    /// <summary>Every format that may be kept, in the order they are put back.</summary>
    public static IReadOnlyList<string> Candidates { get; } =
        [UnicodeText, Text, Html, Rtf, FileDrop, PreferredDropEffect, ShellIdList, Dib, Png];

    /// <summary>The formats to save, in the order to put them back.</summary>
    /// <param name="available">The candidate formats the clipboard holds now.</param>
    public static IReadOnlyList<string> Keep(IReadOnlyCollection<string> available)
    {
        bool hasText = available.Contains(UnicodeText) || available.Contains(Text);
        var keep = new List<string>();
        foreach (var format in Candidates)
        {
            if (!available.Contains(format)) continue;
            // Windows makes the 8-bit text from the Unicode text when only the latter is put back.
            if (format == Text && available.Contains(UnicodeText)) continue;
            if (format is Dib or Png && hasText) continue;
            keep.Add(format);
        }
        return keep;
    }
}
