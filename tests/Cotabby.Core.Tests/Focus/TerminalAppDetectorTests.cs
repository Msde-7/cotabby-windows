using Cotabby.Core.Focus;

namespace Cotabby.Core.Tests.Focus;

public sealed class TerminalAppDetectorTests
{
    [Theory]
    [InlineData("WindowsTerminal")]
    [InlineData("OpenConsole")]
    [InlineData("conhost")]
    [InlineData("cmd")]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    [InlineData("wezterm-gui")]
    [InlineData("alacritty")]
    [InlineData("kitty")]
    [InlineData("mintty")]
    [InlineData("ConEmu")]
    [InlineData("ConEmu64")]
    [InlineData("Hyper")]
    [InlineData("Tabby")]
    [InlineData("warp")]
    [InlineData("rio")]
    [InlineData("putty")]
    [InlineData("wsl")]
    [InlineData("bash")]
    public void IsTerminal_RecognizesEveryKnownEmulatorIgnoringCase(string processName)
    {
        Assert.True(TerminalAppDetector.IsTerminal(processName));
        Assert.True(TerminalAppDetector.IsTerminal(processName.ToUpperInvariant()));
    }

    [Theory]
    [InlineData("chrome")]
    [InlineData("Code")]
    [InlineData("powershell_ise")]
    [InlineData("cmd.exe")]
    [InlineData("")]
    [InlineData(null)]
    public void IsTerminal_RejectsOtherAppsAndNamesThatOnlyStartLikeATerminal(string? processName) =>
        Assert.False(TerminalAppDetector.IsTerminal(processName));

    [Theory]
    [InlineData("xterm-helper-textarea")]
    [InlineData("xterm-screen")]
    [InlineData("focused xterm-helper-textarea")]
    public void IsIntegratedTerminal_AnyXtermPrefixedClass_IsTrue(string classList) =>
        Assert.True(TerminalAppDetector.IsIntegratedTerminal(classList));

    [Theory]
    [InlineData("native-edit-context")]
    [InlineData("monaco-editor no-user-select mac")]
    [InlineData("not-xterm")]
    [InlineData("")]
    [InlineData(null)]
    public void IsIntegratedTerminal_EditorClassesAndEmptyLists_AreFalse(string? classList) =>
        Assert.False(TerminalAppDetector.IsIntegratedTerminal(classList));
}
