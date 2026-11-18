using Cotabby.Win32.Clipboard;
using static Cotabby.Win32.Clipboard.ClipboardFormatPolicy;

namespace Cotabby.Inference.Tests;

public class ClipboardFormatPolicyTests
{
    private const uint Locale = 16;
    private const uint FileDrop = 15;
    private const uint Html = 0xC0A1;
    private const uint Rtf = 0xC0A2;
    private const uint Biff12 = 0xC1F0;
    private const uint Png = 0xC1F1;

    private static ClipboardSaveStep Bytes(uint format) => new(format, ClipboardSaveKind.Bytes);

    [Fact]
    public void RichTextCopy_KeepsEveryTextFormatInOrder()
    {
        Assert.Equal([Bytes(Rtf), Bytes(Html), Bytes(CF_UNICODETEXT)], Plan([Rtf, Html, CF_UNICODETEXT]));
    }

    [Fact]
    public void AnsiText_IsKeptOnlyWithoutUnicodeText()
    {
        Assert.Equal([Bytes(CF_UNICODETEXT), Bytes(Locale)], Plan([CF_UNICODETEXT, Locale, CF_TEXT, CF_OEMTEXT]));
        Assert.Equal([Bytes(CF_TEXT)], Plan([CF_TEXT]));
    }

    [Fact]
    public void SpreadsheetRange_KeepsItsOwnFormatsBesideTheText()
    {
        // A range copied with its formulas must paste back as cells, not as plain text.
        Assert.Equal(
            [Bytes(Biff12), Bytes(Html), Bytes(CF_UNICODETEXT), new(CF_ENHMETAFILE, ClipboardSaveKind.EnhancedMetafile)],
            Plan([Biff12, Html, CF_UNICODETEXT, CF_ENHMETAFILE, CF_METAFILEPICT, CF_TEXT]));
    }

    [Fact]
    public void Picture_KeepsTheBitmapItWasCopiedAsBesideText()
    {
        Assert.Equal(
            [Bytes(Html), Bytes(CF_DIBV5), Bytes(Png), Bytes(CF_UNICODETEXT)],
            Plan([Html, CF_DIBV5, Png, CF_UNICODETEXT, CF_BITMAP, CF_DIB]));
        Assert.Equal([Bytes(CF_DIB)], Plan([CF_BITMAP, CF_PALETTE, CF_DIB, CF_DIBV5]));
    }

    [Fact]
    public void CopiedFiles_KeepEveryFormat()
    {
        Assert.Equal([Bytes(FileDrop), Bytes(0xC010)], Plan([FileDrop, 0xC010]));
    }

    [Theory]
    [InlineData(0x0200u)] // a private format
    [InlineData(0x0300u)] // a GDI object
    [InlineData(0x0080u)] // owner display
    [InlineData(0x0082u)] // a private bitmap's display form
    [InlineData(CF_METAFILEPICT)] // an old metafile alone has a handle inside it
    [InlineData(CF_BITMAP)] // a bitmap handle alone
    public void FormatThatCannotBeCopied_SavesNothing(uint format)
    {
        Assert.Null(Plan([CF_UNICODETEXT, format]));
    }

    [Fact]
    public void EmptyClipboard_KeepsNothing()
    {
        Assert.Equal([], Plan([]));
    }

    [Fact]
    public void PasswordManagerMarkers_MakeTheContentPrivate()
    {
        Assert.True(IsPrivate(excludeFromMonitoring: true, viewerIgnore: false, []));
        Assert.True(IsPrivate(excludeFromMonitoring: false, viewerIgnore: true, []));
        Assert.True(IsPrivate(false, false, BitConverter.GetBytes(0u)));
        Assert.False(IsPrivate(false, false, BitConverter.GetBytes(1u)));
        Assert.False(IsPrivate(false, false, []));
    }
}
