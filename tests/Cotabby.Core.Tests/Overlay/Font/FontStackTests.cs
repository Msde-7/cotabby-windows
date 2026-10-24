using Cotabby.Core.Overlay.Font;

namespace Cotabby.Core.Tests.Overlay.Font;

public sealed class FontStackTests
{
    private readonly FakeGlyphMeasurer _m = new();

    [Fact]
    public void FamiliesAreSplitAndUnquoted() =>
        Assert.Equal(["Google Sans", "Roboto", "Arial"], FontStack.Families("\"Google Sans\", 'Roboto' ,Arial"));

    [Fact]
    public void GenericFamiliesMapToWhatChromiumDrawsOnWindows() =>
        Assert.Equal(
            ["Segoe UI", "Arial", "Times New Roman", "Consolas"],
            FontStack.Families("system-ui, sans-serif, serif, monospace"));

    [Fact]
    public void MacOnlyNamesAreSkipped() =>
        Assert.Equal(["Segoe UI", "Arial"], FontStack.Families("-apple-system, BlinkMacSystemFont, \"Segoe UI\", Arial"));

    [Fact]
    public void FirstInstalledSkipsMissingFamilies()
    {
        Assert.Equal("Georgia", FontStack.FirstInstalled("Inter, Georgia, serif", _m));
        Assert.Equal("Arial", FontStack.FirstInstalled("Inter, sans-serif", _m));
        Assert.Null(FontStack.FirstInstalled("Inter, \"Google Sans\"", _m));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void NothingNamedIsAnEmptyStack(string? name) => Assert.Empty(FontStack.Families(name));
}
