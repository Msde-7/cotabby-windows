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
    private const uint DataObject = 0xC009;
    private const uint OlePrivateData = 0xC013;
    private const uint EmbedSource = 0xC00B;
    private const uint ObjectDescriptor = 0xC00E;
    private const uint CanIncludeInHistory = 0xC2A0;
    private const uint MozUrl = 0xC2B0;
    private const uint Sylk = 4;

    private static readonly ClipboardRegisteredFormats Registered = new(
        new HashSet<uint> { Png }, new HashSet<uint> { DataObject, OlePrivateData })
    {
        RichApp = new HashSet<uint> { Rtf, Biff12, EmbedSource, ObjectDescriptor },
        Embedding = new HashSet<uint> { EmbedSource, ObjectDescriptor },
        Markers = new HashSet<uint> { CanIncludeInHistory },
    };

    private static ClipboardSaveStep Bytes(uint format) => new(format, ClipboardSaveKind.Bytes);

    [Fact]
    public void RichTextCopy_KeepsEveryTextFormatInOrder()
    {
        Assert.Equal([Bytes(Rtf), Bytes(Html), Bytes(CF_UNICODETEXT)], Plan([Rtf, Html, CF_UNICODETEXT], Registered));
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
        // A range copied with its formulas must paste back as cells, not as plain text. Its
        // pictures and its OLE embedding are renderings of the range that can take seconds to
        // make, so they are left out.
        Assert.Equal(
            [Bytes(Biff12), Bytes(Html), Bytes(CF_UNICODETEXT)],
            Plan([Biff12, Html, CF_UNICODETEXT, CF_ENHMETAFILE, CF_METAFILEPICT, CF_BITMAP, Png, EmbedSource, ObjectDescriptor,
                CF_TEXT, CF_DIB], Registered));
        Assert.Equal([Bytes(Sylk), Bytes(CF_UNICODETEXT)], Plan([Sylk, CF_UNICODETEXT, CF_DIB], Registered));
    }

    [Fact]
    public void DocumentSelection_LeavesItsPicturesOut()
    {
        Assert.Equal(
            [Bytes(Rtf), Bytes(Html), Bytes(CF_UNICODETEXT)],
            Plan([Rtf, Html, CF_UNICODETEXT, CF_ENHMETAFILE, CF_DIB, Png, EmbedSource], Registered));
    }

    [Fact]
    public void Picture_KeepsTheBitmapItWasCopiedAsBesideText()
    {
        // A browser's Copy Image puts the image's address beside the picture. The picture is what
        // was copied, so it is kept.
        Assert.Equal(
            [Bytes(MozUrl), Bytes(Html), Bytes(CF_DIBV5), Bytes(Png), Bytes(CF_UNICODETEXT)],
            Plan([MozUrl, Html, CF_DIBV5, Png, CF_UNICODETEXT, CF_BITMAP, CF_DIB], Registered));
        Assert.Equal(
            [Bytes(CF_TEXT), Bytes(CF_DIB), new(CF_ENHMETAFILE, ClipboardSaveKind.EnhancedMetafile)],
            Plan([CF_TEXT, CF_DIB, CF_ENHMETAFILE], Registered));
    }

    [Fact]
    public void PictureFromAnOleServer_KeepsThePictureAndTheEmbedding()
    {
        // Paint offers its picture as an embedded object too. Without text the picture is the content.
        Assert.Equal(
            [Bytes(EmbedSource), Bytes(ObjectDescriptor), Bytes(CF_DIB)],
            Plan([EmbedSource, ObjectDescriptor, CF_METAFILEPICT, CF_DIB, CF_BITMAP], Registered));
    }

    [Fact]
    public void PictureCopiedAlone_KeepsTheBitmapItWasCopiedAs()
    {
        Assert.Equal([Bytes(Html), Bytes(CF_DIBV5), Bytes(Png)], Plan([Html, CF_DIBV5, Png, CF_BITMAP, CF_DIB], Registered));
        Assert.Equal([Bytes(CF_DIB)], Plan([CF_BITMAP, CF_PALETTE, CF_DIB, CF_DIBV5]));
        // An old metafile is kept through the enhanced one Windows makes from it.
        Assert.Equal([new(CF_ENHMETAFILE, ClipboardSaveKind.EnhancedMetafile)], Plan([CF_METAFILEPICT, CF_ENHMETAFILE]));
    }

    [Fact]
    public void OleLinkToTheCopyingApp_IsLeftOut()
    {
        // The paste releases the app's data object, so a restored link would point at nothing.
        Assert.Equal(
            [Bytes(Biff12), Bytes(CF_UNICODETEXT)],
            Plan([DataObject, Biff12, OlePrivateData, CF_UNICODETEXT], Registered));
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
    public void FormatThatCannotBeCopied_IsLeftOutAndTheRestKept(uint format)
    {
        // An app the content is pasted into could not use these either.
        Assert.Equal([Bytes(Html)], Plan([Html, format]));
    }

    [Fact]
    public void EmptyClipboard_KeepsNothing()
    {
        Assert.Equal([], Plan([]));
    }

    [Fact]
    public void EmptyClipboard_CanBeSwapped()
    {
        Assert.True(CanSwap([], [], [], Registered));
    }

    [Fact]
    public void SavedText_CanBeSwapped()
    {
        uint[] available = [Html, CF_UNICODETEXT, Locale];
        Assert.True(CanSwap(available, Plan(available, Registered), [Html, CF_UNICODETEXT, Locale], Registered));
    }

    [Fact]
    public void TextTheAppCouldNotMake_StopsTheSwap()
    {
        // A failed delayed render, such as from a remote clipboard, leaves no text to put back.
        uint[] available = [Html, CF_UNICODETEXT, Locale];
        Assert.False(CanSwap(available, Plan(available, Registered), [Html, Locale], Registered));
    }

    [Fact]
    public void OnlySideFormatsSaved_StopsTheSwap()
    {
        // The picture's app exited, so only the locale and the history marker came back.
        uint[] available = [CF_DIB, Png, Locale, CanIncludeInHistory];
        Assert.False(CanSwap(available, Plan(available, Registered), [Locale, CanIncludeInHistory], Registered));
    }

    [Fact]
    public void OnlyAPrivateFormat_StopsTheSwap()
    {
        uint[] available = [0x0200];
        Assert.False(CanSwap(available, Plan(available, Registered), [], Registered));
    }

    [Fact]
    public void APictureThatCouldNotBeMade_DoesNotStopTheSwapWhenTheRestIsSaved()
    {
        uint[] available = [MozUrl, CF_DIB, Png];
        Assert.True(CanSwap(available, Plan(available, Registered), [MozUrl, Png], Registered));
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
