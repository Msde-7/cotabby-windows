using System.IO;
using System.Text.Json;
using Cotabby.App.Settings;
using Cotabby.Core.Input;

namespace Cotabby.Inference.Tests;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"cotabby-settings-{Guid.NewGuid():N}");
    private readonly SettingsStore _store;

    public SettingsStoreTests() => _store = SettingsStore.InDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void CorruptFile_IsBackedUpBeforeTheDefaultsLoad()
    {
        File.WriteAllText(_store.Path, "{ \"Enabled\": fal");

        var settings = _store.Load();

        Assert.True(settings.Enabled);
        var backup = Assert.Single(Directory.GetFiles(_directory, "settings.json.corrupt-*"));
        Assert.Equal("{ \"Enabled\": fal", File.ReadAllText(backup));
    }

    [Fact]
    public void FileThatIsNotAnObject_IsBackedUp()
    {
        File.WriteAllText(_store.Path, "[1, 2]");

        _store.Load();

        Assert.Single(Directory.GetFiles(_directory, "settings.json.corrupt-*"));
    }

    [Fact]
    public void NullLists_BecomeEmptyOrDefault()
    {
        var settings = SettingsStore.Parse("""
            {
              "TypingHistoryExcludedApps": null,
              "BlockedApps": null,
              "ShortcutOverrides": null,
              "Languages": null,
              "UserName": null,
              "Enabled": false
            }
            """);

        Assert.Empty(settings.TypingHistoryExcludedApps);
        Assert.Empty(settings.BlockedApps);
        Assert.Empty(settings.ShortcutOverrides);
        Assert.NotEmpty(settings.Languages);
        Assert.Equal("", settings.UserName);
        Assert.False(settings.Enabled);
        // The settings that read the lists must not throw.
        _ = settings.ToSuggestionSettings();
        _ = settings.TypingHistoryPreferences;
    }

    [Fact]
    public void NullEntries_AreDroppedFromNameLists()
    {
        var settings = SettingsStore.Parse("""{ "BlockedApps": ["notepad", null] }""");

        Assert.Equal(["notepad"], settings.BlockedApps);
    }

    [Theory]
    [InlineData("42")]
    [InlineData("true")]
    [InlineData("{}")]
    [InlineData("[]")]
    public void LegacyAcceptKeyOfAnotherType_KeepsTheDefault(string value)
    {
        var settings = SettingsStore.Parse($$"""{ "AcceptSuggestionKey": {{value}}, "Enabled": false }""");

        Assert.Equal(KeyBinding.Backtick, settings.AcceptAll);
        Assert.False(settings.Enabled);
    }

    [Fact]
    public void LegacyAcceptKeyName_IsStillMigrated()
    {
        var settings = SettingsStore.Parse("""{ "AcceptSuggestionKey": "tilde" }""");

        Assert.Equal(KeyBinding.VkBacktick, settings.AcceptAll.VirtualKey);
        Assert.Equal(KeyModifiers.Shift, settings.AcceptAll.Modifiers);
    }

    [Fact]
    public void Save_RoundTripsAndLeavesNoTemporaryFile()
    {
        var settings = new AppSettings { UserName = "Ada", BlockedApps = ["slack"] };

        _store.Save(settings);
        var loaded = _store.Load();

        Assert.Equal("Ada", loaded.UserName);
        Assert.Equal(["slack"], loaded.BlockedApps);
        Assert.False(File.Exists(_store.Path + ".tmp"));
        Assert.Empty(Directory.GetFiles(_directory, "settings.json.corrupt-*"));
        Assert.NotNull(JsonDocument.Parse(File.ReadAllText(_store.Path)));
    }
}
