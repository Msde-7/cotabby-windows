using Cotabby.Core.Support;

namespace Cotabby.Core.Tests.Support;

public sealed class SettingsValuesTests
{
    [Theory]
    [InlineData(0, 10)]
    [InlineData(-5, 10)]
    [InlineData(20, 20)]
    [InlineData(900, 500)]
    public void DebounceMs_IsFlooredAndCapped(int stored, int expected) =>
        Assert.Equal(expected, SettingsValues.DebounceMs(stored));

    [Theory]
    [InlineData(0.0, 0.05)]
    [InlineData(0.2, 0.2)]
    [InlineData(2.0, 0.30)]
    [InlineData(double.NaN, 0.10)]
    public void FadeSeconds_StaysInsideTheSliderRange(double stored, double expected) =>
        Assert.Equal(expected, SettingsValues.FadeSeconds(stored));

    [Theory]
    [InlineData(0.0, 0.3)]
    [InlineData(0.5, 0.5)]
    [InlineData(4.0, 1.0)]
    [InlineData(double.PositiveInfinity, 1.0)]
    public void Opacity_StaysVisible(double stored, double expected) =>
        Assert.Equal(expected, SettingsValues.Opacity(stored));

    [Theory]
    [InlineData(0.1, 0.7)]
    [InlineData(1.2, 1.2)]
    [InlineData(3.0, 1.3)]
    [InlineData(double.NaN, 1.0)]
    public void SizeMultiplier_StaysInsideTheSliderRange(double stored, double expected) =>
        Assert.Equal(expected, SettingsValues.SizeMultiplier(stored));

    [Theory]
    [InlineData("#3b82f6", "#3B82F6")]
    [InlineData(" 3B82F6 ", "#3B82F6")]
    [InlineData("auto", "auto")]
    [InlineData("#3B82F", "auto")]
    [InlineData("#3B82F6FF", "auto")]
    [InlineData("#GGGGGG", "auto")]
    [InlineData("", "auto")]
    [InlineData(null, "auto")]
    public void GhostColor_KeepsSixHexDigitsOrFallsBackToAutomatic(string? stored, string expected) =>
        Assert.Equal(expected, SettingsValues.GhostColor(stored));
}
