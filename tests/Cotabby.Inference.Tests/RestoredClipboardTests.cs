using Cotabby.Win32.Clipboard;

namespace Cotabby.Inference.Tests;

public class RestoredClipboardTests
{
    [Fact]
    public void ContentPutBack_KeepsThePrivacyItHadBeforeThePaste()
    {
        var restored = new RestoredClipboard(41, IsPrivate: false);
        // Cotabby's own markers on the restored content read as private.
        Assert.False(RestoredClipboard.IsPrivateAt(restored, 41, markersSayPrivate: true));
        Assert.True(RestoredClipboard.IsPrivateAt(new RestoredClipboard(41, IsPrivate: true), 41, markersSayPrivate: true));
    }

    [Fact]
    public void ContentCopiedSince_IsJudgedByItsMarkers()
    {
        var restored = new RestoredClipboard(41, IsPrivate: false);
        Assert.True(RestoredClipboard.IsPrivateAt(restored, 42, markersSayPrivate: true));
        Assert.False(RestoredClipboard.IsPrivateAt(null, 42, markersSayPrivate: false));
    }

    [Fact]
    public void APasteThatPutNothingBack_KeepsTheLastRestoreWhileTheClipboardHoldsIt()
    {
        var last = new RestoredClipboard(41, IsPrivate: false);
        Assert.Equal(last, RestoredClipboard.After(last, latest: null, now: 41));
        Assert.Null(RestoredClipboard.After(last, latest: null, now: 45));
        var latest = new RestoredClipboard(47, IsPrivate: true);
        Assert.Equal(latest, RestoredClipboard.After(last, latest, now: 47));
    }
}
