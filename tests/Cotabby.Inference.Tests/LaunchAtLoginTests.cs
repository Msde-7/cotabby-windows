using Cotabby.App.Settings;

namespace Cotabby.Inference.Tests;

public sealed class LaunchAtLoginTests
{
    [Theory]
    [InlineData("\"C:\\Program Files\\Cotabby\\Cotabby.exe\"", "C:\\Program Files\\Cotabby\\Cotabby.exe")]
    [InlineData("\"C:\\Cotabby\\Cotabby.exe\" --minimized", "C:\\Cotabby\\Cotabby.exe")]
    [InlineData("C:\\Cotabby\\Cotabby.exe --minimized", "C:\\Cotabby\\Cotabby.exe")]
    [InlineData("  C:\\Cotabby\\Cotabby.exe  ", "C:\\Cotabby\\Cotabby.exe")]
    [InlineData("\"\"", null)]
    [InlineData("\"C:\\unterminated", null)]
    [InlineData("", null)]
    public void TargetPath_ReadsTheProgramFromARunValue(string value, string? expected) =>
        Assert.Equal(expected, LaunchAtLogin.TargetPath(value));
}
