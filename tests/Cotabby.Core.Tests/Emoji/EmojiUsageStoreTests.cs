using Cotabby.Core.Emoji;

namespace Cotabby.Core.Tests.Emoji;

public sealed class EmojiUsageStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"cotabby-emoji-usage-{Guid.NewGuid():N}.json");

    public void Dispose() => File.Delete(_path);

    [Fact]
    public void RecordPlacesMostRecentFirstAndCountsFrequency()
    {
        var sut = new EmojiUsageStore(_path);
        sut.Record("joy");
        sut.Record("fire");
        sut.Record("joy");

        var snapshot = sut.Snapshot();
        Assert.Equal(["joy", "fire"], snapshot.RecentAliases);
        Assert.Equal(2, snapshot.Frequency["joy"]);
        Assert.Equal(1, snapshot.Frequency["fire"]);
    }

    [Fact]
    public void RecordNormalizesAndIgnoresBlank()
    {
        var sut = new EmojiUsageStore(_path);
        sut.Record("  JOY  ");
        sut.Record("   ");

        var snapshot = sut.Snapshot();
        Assert.Equal(["joy"], snapshot.RecentAliases);
        Assert.Equal(1, snapshot.Frequency["joy"]);
    }

    [Fact]
    public void RecentsAreCappedKeepingNewest()
    {
        var sut = new EmojiUsageStore(_path);
        for (int index = 0; index < 60; index++) sut.Record($"alias{index}");

        var recents = sut.Snapshot().RecentAliases;
        Assert.Equal(50, recents.Count);
        Assert.Equal("alias59", recents[0]);
        Assert.DoesNotContain("alias0", recents);
    }

    [Fact]
    public void FrequencyIsBoundedAndKeepsHeavyHitters()
    {
        var sut = new EmojiUsageStore(_path);
        // A heavy favorite that must survive trimming even after it falls out of recents.
        for (int i = 0; i < 10; i++) sut.Record("joy");
        for (int index = 0; index < 320; index++) sut.Record($"flood{index}");

        var snapshot = sut.Snapshot();
        // The 301st unique alias trips the cap and trims down to the 200 target, then the remaining
        // 20 flood inserts land afterwards.
        Assert.Equal(220, snapshot.Frequency.Count);
        Assert.Equal(10, snapshot.Frequency["joy"]);
        Assert.All(snapshot.RecentAliases, alias => Assert.True(snapshot.Frequency.ContainsKey(alias)));
    }

    [Fact]
    public void ClearForgetsEverything()
    {
        var sut = new EmojiUsageStore(_path);
        sut.Record("joy");
        sut.Clear();

        Assert.Empty(sut.Snapshot().RecentAliases);
        Assert.Empty(sut.Snapshot().Frequency);
        Assert.Empty(new EmojiUsageStore(_path).Snapshot().RecentAliases);
    }

    [Fact]
    public void CorruptPersistedFileStartsEmpty()
    {
        File.WriteAllText(_path, "not json");

        var snapshot = new EmojiUsageStore(_path).Snapshot();
        Assert.Empty(snapshot.RecentAliases);
        Assert.Empty(snapshot.Frequency);
    }

    [Fact]
    public void StatePersistsAcrossInstances()
    {
        new EmojiUsageStore(_path).Record("rocket");

        // The persisted shape matches upstream's blob, a recents array and a frequency map.
        Assert.Equal("""{"recents":["rocket"],"frequency":{"rocket":1}}""", File.ReadAllText(_path));
        var reopened = new EmojiUsageStore(_path).Snapshot();
        Assert.Equal(["rocket"], reopened.RecentAliases);
        Assert.Equal(1, reopened.Frequency["rocket"]);
    }

    [Fact]
    public void SnapshotIsNotAffectedByLaterRecords()
    {
        var sut = new EmojiUsageStore(_path);
        sut.Record("joy");
        var snapshot = sut.Snapshot();
        sut.Record("fire");

        Assert.Equal(["joy"], snapshot.RecentAliases);
        Assert.False(snapshot.Frequency.ContainsKey("fire"));
    }

    [Fact]
    public void IsFavoriteHonorsRecencyAndFrequency()
    {
        var recentOnly = new EmojiUsageSnapshot(["wave"], new Dictionary<string, int>());
        Assert.True(recentOnly.IsFavorite("wave"));
        Assert.False(recentOnly.IsFavorite("fire"));

        var frequentEnough = new EmojiUsageSnapshot([], new Dictionary<string, int> { ["fire"] = 2 });
        Assert.True(frequentEnough.IsFavorite("fire"));

        var usedOnce = new EmojiUsageSnapshot([], new Dictionary<string, int> { ["fire"] = 1 });
        Assert.False(usedOnce.IsFavorite("fire"));

        // The matcher may pass the alias as typed while stored keys are lowercased.
        Assert.True(recentOnly.IsFavorite("WAVE"));
        Assert.True(frequentEnough.IsFavorite("Fire"));
    }
}
