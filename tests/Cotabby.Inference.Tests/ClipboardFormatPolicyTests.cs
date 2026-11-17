using Cotabby.Win32.Clipboard;
using static Cotabby.Win32.Clipboard.ClipboardFormatPolicy;

namespace Cotabby.Inference.Tests;

public class ClipboardFormatPolicyTests
{
    [Fact]
    public void RichTextCopy_KeepsEveryTextFormat()
    {
        Assert.Equal([UnicodeText, Html, Rtf], Keep([Rtf, Html, UnicodeText]));
    }

    [Fact]
    public void AnsiText_IsKeptOnlyWithoutUnicodeText()
    {
        Assert.Equal([UnicodeText], Keep([UnicodeText, Text]));
        Assert.Equal([Text], Keep([Text]));
    }

    [Fact]
    public void CopiedFiles_KeepTheirCutOrCopyEffect()
    {
        Assert.Equal([FileDrop, PreferredDropEffect, ShellIdList], Keep([ShellIdList, PreferredDropEffect, FileDrop]));
    }

    [Fact]
    public void Picture_IsKeptWhenNoTextCameWithIt()
    {
        Assert.Equal([Html, Dib, Png], Keep([Png, Dib, Html]));
    }

    [Fact]
    public void Picture_IsLeftOutBesideText()
    {
        Assert.Equal([UnicodeText, Html], Keep([UnicodeText, Html, Dib, Png]));
    }

    [Fact]
    public void EmptyClipboard_KeepsNothing()
    {
        Assert.Empty(Keep([]));
    }
}
