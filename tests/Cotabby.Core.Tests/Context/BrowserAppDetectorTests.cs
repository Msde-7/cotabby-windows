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
}
