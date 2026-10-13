using Cotabby.Core.Context;

namespace Cotabby.Core.Tests.Context;

public sealed class AppSurfaceClassifierTests
{
    [Theory]
    [InlineData("WindowsTerminal", AppSurfaceClass.Terminal)]
    [InlineData("pwsh", AppSurfaceClass.Terminal)]
    [InlineData("Code", AppSurfaceClass.CodeEditor)]
    [InlineData("Code - Insiders", AppSurfaceClass.CodeEditor)]
    [InlineData("Cursor", AppSurfaceClass.CodeEditor)]
    [InlineData("VSCodium", AppSurfaceClass.CodeEditor)]
    [InlineData("devenv", AppSurfaceClass.CodeEditor)]
    [InlineData("rider64", AppSurfaceClass.CodeEditor)]
    [InlineData("idea64", AppSurfaceClass.CodeEditor)]
    [InlineData("pycharm64", AppSurfaceClass.CodeEditor)]
    [InlineData("webstorm64", AppSurfaceClass.CodeEditor)]
    [InlineData("sublime_text", AppSurfaceClass.CodeEditor)]
    [InlineData("notepad++", AppSurfaceClass.CodeEditor)]
    [InlineData("OUTLOOK", AppSurfaceClass.Email)]
    [InlineData("olk", AppSurfaceClass.Email)]
    [InlineData("HxOutlook", AppSurfaceClass.Email)]
    [InlineData("thunderbird", AppSurfaceClass.Email)]
    [InlineData("slack", AppSurfaceClass.Chat)]
    [InlineData("ms-teams", AppSurfaceClass.Chat)]
    [InlineData("Teams", AppSurfaceClass.Chat)]
    [InlineData("Discord", AppSurfaceClass.Chat)]
    [InlineData("Telegram", AppSurfaceClass.Chat)]
    [InlineData("WhatsApp", AppSurfaceClass.Chat)]
    [InlineData("Signal", AppSurfaceClass.Chat)]
    [InlineData("chrome", AppSurfaceClass.Browser)]
    [InlineData("msedge", AppSurfaceClass.Browser)]
    [InlineData("firefox", AppSurfaceClass.Browser)]
    [InlineData("arc", AppSurfaceClass.Browser)]
    public void Classify_RecognizesEachCuratedFamilyIgnoringCase(string processName, AppSurfaceClass expected)
    {
        Assert.Equal(expected, AppSurfaceClassifier.Classify(processName));
        Assert.Equal(expected, AppSurfaceClassifier.Classify(processName.ToUpperInvariant()));
        Assert.Equal(expected, AppSurfaceClassifier.Classify(processName.ToLowerInvariant()));
    }

    [Theory]
    [InlineData("chrome")]
    [InlineData("Code")]
    [InlineData("slack")]
    [InlineData(null)]
    public void Classify_IntegratedTerminalBeatsEverything(string? processName)
    {
        Assert.Equal(AppSurfaceClass.Terminal, AppSurfaceClassifier.Classify(processName, isIntegratedTerminal: true));
    }

    [Theory]
    [InlineData("notepad")]
    [InlineData("chrome.exe")]
    [InlineData("")]
    [InlineData(null)]
    public void Classify_UnknownAndMissingNamesAreOther(string? processName)
    {
        Assert.Equal(AppSurfaceClass.Other, AppSurfaceClassifier.Classify(processName));
    }
}
