using Cotabby.Core.Context;

namespace Cotabby.Core.Tests.Context;

public sealed class BrowserAppDetectorTests
{
    [Theory]
    [InlineData("chrome")]
    [InlineData("msedge")]
    [InlineData("firefox")]
    [InlineData("brave")]
    [InlineData("opera")]
    [InlineData("vivaldi")]
    [InlineData("arc")]
    public void IsBrowser_CoversEveryFamilyIgnoringCase(string processName)
    {
        Assert.True(BrowserAppDetector.IsBrowser(processName));
        Assert.True(BrowserAppDetector.IsBrowser(processName.ToUpperInvariant()));
    }

    [Theory]
    [InlineData("WindowsTerminal")]
    [InlineData("Code")]
    [InlineData("")]
    [InlineData(null)]
    public void IsBrowser_RejectsEverythingElse(string? processName)
    {
        Assert.False(BrowserAppDetector.IsBrowser(processName));
    }

    [Theory]
    [InlineData("chrome")]
    [InlineData("MSEDGE")]
    [InlineData("brave")]
    [InlineData("vivaldi")]
    public void IsChromiumBrowser_CoversChromiumFamilies(string processName)
    {
        Assert.True(BrowserAppDetector.IsChromiumBrowser(processName));
        Assert.True(BrowserAppDetector.NeedsWebAccessibilityPriming(processName));
    }

    [Fact]
    public void IsChromiumBrowser_ExcludesFirefox()
    {
        // Firefox is a browser for tone hints, but its tree needs no waking.
        Assert.True(BrowserAppDetector.IsBrowser("firefox"));
        Assert.False(BrowserAppDetector.IsChromiumBrowser("firefox"));
        Assert.False(BrowserAppDetector.NeedsWebAccessibilityPriming("firefox"));
    }

    [Theory]
    [InlineData("Code")]
    [InlineData("code")]
    [InlineData("Code - Insiders")]
    [InlineData("VSCodium")]
    [InlineData("Obsidian")]
    [InlineData("ClickUp")]
    [InlineData("Codex")]
    [InlineData("ChatGPT")]
    [InlineData("Slack")]
    [InlineData("Discord")]
    [InlineData("Notion")]
    public void IsElectronEditor_CoversTheNamedEditors(string processName)
    {
        Assert.True(BrowserAppDetector.IsElectronEditor(processName));
        Assert.True(BrowserAppDetector.NeedsWebAccessibilityPriming(processName));
        Assert.False(BrowserAppDetector.IsBrowser(processName));
    }

    [Theory]
    [InlineData("CodeHelper")]
    [InlineData("Code.exe")]
    [InlineData("Teams")]
    [InlineData("notepad")]
    [InlineData("")]
    [InlineData(null)]
    public void IsElectronEditor_IsAnExactList(string? processName)
    {
        Assert.False(BrowserAppDetector.IsElectronEditor(processName));
        Assert.False(BrowserAppDetector.NeedsWebAccessibilityPriming(processName));
    }
}
