using Cotabby.App.Hosting;
using Cotabby.App.Settings;
using Cotabby.Core.Models;

namespace Cotabby.Inference.Tests;

public sealed class ModelSelectionTests
{
    private static readonly CotabbyModel First = Model("first");
    private static readonly CotabbyModel Small = Model("small");
    private static readonly CotabbyModel Large = Model("large");
    private static readonly CotabbyModel[] Catalog = [First, Small, Large];

    private readonly HashSet<string> _onDisk = [];

    private ModelSelection Selection(AppSettings settings, bool onBattery = false) =>
        new(settings, onBattery, m => _onDisk.Contains(m.Id), id => Catalog.FirstOrDefault(m => m.Id == id), First);

    [Fact]
    public void WithoutPowerSwitching_TheActiveModelIsSelectedAndLoaded()
    {
        _onDisk.Add("large");
        var settings = new AppSettings { ActiveModelId = "large", BatteryModelId = "small", PluggedInModelId = "small" };

        var selection = Selection(settings, onBattery: true);

        Assert.Null(selection.PowerSlotId);
        Assert.Equal("large", selection.SelectedModelId);
        Assert.Same(Large, selection.ModelToLoad);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("removed-model")]
    public void AMissingOrUnknownActiveModel_FallsBackToTheFirstModel(string? activeId)
    {
        var selection = Selection(new AppSettings { ActiveModelId = activeId });

        Assert.Same(First, selection.ActiveModel);
        Assert.Equal("first", selection.SelectedModelId);
        Assert.Same(First, selection.ModelToLoad);
    }

    [Theory]
    [InlineData(true, "small")]
    [InlineData(false, "large")]
    public void PowerSwitching_LoadsTheModelOfTheCurrentPowerSource(bool onBattery, string expected)
    {
        _onDisk.UnionWith(["small", "large"]);
        var settings = new AppSettings
        {
            ActiveModelId = "first",
            PowerModelSwitching = true,
            BatteryModelId = "small",
            PluggedInModelId = "large",
        };

        var selection = Selection(settings, onBattery);

        Assert.Equal(expected, selection.PowerSlotId);
        Assert.Equal(expected, selection.SelectedModelId);
        Assert.Equal(expected, selection.ModelToLoad.Id);
    }

    [Fact]
    public void PowerSwitching_FallsBackToTheActiveModelWhileTheSlotModelIsNotOnDisk()
    {
        _onDisk.Add("first");
        var settings = new AppSettings { ActiveModelId = "first", PowerModelSwitching = true, PluggedInModelId = "large" };

        var selection = Selection(settings);

        Assert.Null(selection.PowerSourceModel);
        // The lists still mark the pick, so its download can be started there.
        Assert.Equal("large", selection.SelectedModelId);
        Assert.Same(First, selection.ModelToLoad);
    }

    [Fact]
    public void PowerSwitching_WithAnEmptySlot_UsesTheActiveModel()
    {
        var settings = new AppSettings { ActiveModelId = "small", PowerModelSwitching = true, BatteryModelId = "" };

        var selection = Selection(settings, onBattery: true);

        Assert.Null(selection.PowerSlotId);
        Assert.Equal("small", selection.SelectedModelId);
        Assert.Same(Small, selection.ModelToLoad);
    }

    [Fact]
    public void PowerSwitching_WithAnUnknownSlotModel_UsesTheActiveModel()
    {
        var settings = new AppSettings { ActiveModelId = "small", PowerModelSwitching = true, PluggedInModelId = "removed-model" };

        var selection = Selection(settings);

        Assert.Equal("small", selection.SelectedModelId);
        Assert.Same(Small, selection.ModelToLoad);
    }

    [Theory]
    [InlineData(false, false, "active")]
    [InlineData(true, true, "battery")]
    [InlineData(true, false, "plugged")]
    public void Pick_WritesTheSlotThatIsInUse(bool switching, bool onBattery, string slot)
    {
        var settings = new AppSettings { ActiveModelId = "first", PowerModelSwitching = switching };

        ModelSelection.Pick(settings, "large", onBattery);

        Assert.Equal(slot == "active" ? "large" : "first", settings.ActiveModelId);
        Assert.Equal(slot == "battery" ? "large" : "", settings.BatteryModelId);
        Assert.Equal(slot == "plugged" ? "large" : "", settings.PluggedInModelId);
    }

    private static CotabbyModel Model(string id) => new()
    {
        Id = id,
        DisplayName = id,
        DownloadUrl = $"https://example.invalid/{id}.gguf",
        ApproxSizeBytes = 1,
        ContextLength = 4096,
    };
}
