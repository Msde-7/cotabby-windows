using Cotabby.Core.Overlay.Font;
using K = Cotabby.Core.Overlay.Font.HostZoomLadder.Kind;

namespace Cotabby.Core.Tests.Overlay.Font;

/// <summary>Each case is a size measured from a host's text against the size it reports.</summary>
public sealed class HostZoomLadderTests
{
    private static double? Snapped(double measured, double reported, K kind) =>
        HostZoomLadder.SnappedSize(measured, reported, kind);

    [Fact]
    public void AnElectronHalfLevelIsSnappedToExactly()
    {
        Assert.Equal(14 * 1.0954451150103321, Snapped(15.4, 14, K.Electron)!.Value, 4);
        Assert.Equal(15.336, Snapped(15.30, 14, K.Electron)!.Value, 3);
    }

    [Fact]
    public void AMeasurementNearTheReportedSizeIsTheReportedSize()
    {
        Assert.Equal(16, Snapped(16.096, 16, K.Electron));
        Assert.Equal(16, Snapped(15.992, 16, K.Electron));
        Assert.Equal(15, Snapped(14.91, 15, K.ChromeBrowser));
    }

    [Fact]
    public void ChromeZoomsThroughItsPresets()
    {
        Assert.Equal(15.4, Snapped(15.37, 14, K.ChromeBrowser)!.Value, 4);
        Assert.Equal(17.5, Snapped(17.4, 14, K.ChromeBrowser)!.Value, 4);
    }

    [Fact]
    public void AMeasurementBetweenStepsKeepsItsOwnSize()
    {
        Assert.Null(Snapped(15.0, 14, K.Electron));
        Assert.Null(Snapped(24, 16, K.Electron));
        Assert.Equal(24, Snapped(24, 16, K.ChromeBrowser));
        Assert.Null(Snapped(20.8, 16, K.Electron));
    }

    [Fact]
    public void DegenerateInputsSnapNothing()
    {
        Assert.Null(Snapped(0, 14, K.Electron));
        Assert.Null(Snapped(15, 0, K.ChromeBrowser));
        Assert.Null(Snapped(double.PositiveInfinity, 14, K.Electron));
        Assert.Null(Snapped(double.NaN, 14, K.Electron));
    }

    [Theory]
    [InlineData("chrome", K.ChromeBrowser)]
    [InlineData("msedge", K.ChromeBrowser)]
    [InlineData("Code", K.Electron)]
    [InlineData("Obsidian", K.Electron)]
    public void ChromiumHostsHaveALadder(string process, K expected) =>
        Assert.Equal(expected, HostZoomLadder.For(process));

    [Theory]
    [InlineData("firefox")]
    [InlineData("notepad")]
    [InlineData(null)]
    public void OtherHostsHaveNone(string? process) => Assert.Null(HostZoomLadder.For(process));
}
