using Cotabby.Core.Suggestions;

namespace Cotabby.Core.Tests.Suggestions;

/// <summary>
/// Ported from upstream's SuggestionAnchorCacheTests. Pins the one match rule behind instant
/// re-shows: the live tail is an anchor's tail plus the first k suggested characters, k short of
/// the whole suggestion, deepest and then newest match first.
/// </summary>
public sealed class SuggestionAnchorCacheTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.FromUnixTimeSeconds(1_000_000);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private readonly Clock _clock = new();

    private SuggestionAnchorCache MakeCache() => new(_clock);

    [Fact]
    public void FreshAnchorMatchesAtZeroConsumed()
    {
        var cache = MakeCache();
        cache.Record("1", "Hello", " world again");
        Assert.Equal(" world again", cache.Remainder("1", "Hello"));
    }

    [Fact]
    public void TheCutFallsBetweenWhatTheUserSeesAsCharacters()
    {
        var cache = MakeCache();
        // An accent typed as a combining mark is one character with its letter, so the plain letter
        // is not on the way to it and nothing starts with the lone mark.
        cache.Record("1", "caf", "e\u0301 au lait");
        Assert.Null(cache.Remainder("1", "cafe"));
        Assert.Equal(" au lait", cache.Remainder("1", "cafe\u0301"));
    }

    [Fact]
    public void TypeThroughThenBackspaceRollbackBothResolveFromOneAnchor()
    {
        var cache = MakeCache();
        cache.Record("1", "Hello", " world again");
        // Lookups never use the entry up.
        Assert.Equal("rld again", cache.Remainder("1", "Hello wo"));
        Assert.Equal(" world again", cache.Remainder("1", "Hello"));
    }

    [Fact]
    public void FullyConsumedSuggestionNeverReoffersItsTail()
    {
        var cache = MakeCache();
        cache.Record("1", "Hello", " world");
        Assert.Null(cache.Remainder("1", "Hello world"));
    }

    [Fact]
    public void DivergentTypingDoesNotMatch()
    {
        var cache = MakeCache();
        cache.Record("1", "Hello", " world again");
        Assert.Null(cache.Remainder("1", "Hello wa"));
    }

    [Fact]
    public void DifferentFieldDoesNotMatch()
    {
        var cache = MakeCache();
        cache.Record("1", "Hello", " world");
        Assert.Null(cache.Remainder("2", "Hello"));
    }

    [Fact]
    public void DeepestConsumedMatchWins()
    {
        var cache = MakeCache();
        cache.Record("1", "Hello", " world again");
        cache.Record("1", "Hello wo", "rld forever");
        // Both fit "Hello wor": the first at k=4, the second at k=1. The deeper one resumes closest to the caret.
        Assert.Equal("ld again", cache.Remainder("1", "Hello wor"));
    }

    [Fact]
    public void EntriesExpire()
    {
        var cache = MakeCache();
        cache.Record("1", "Hello", " world");
        _clock.Now += SuggestionAnchorCache.MaxEntryAge + TimeSpan.FromSeconds(1);
        Assert.Null(cache.Remainder("1", "Hello"));
    }

    [Fact]
    public void CapacityEvictsOldest()
    {
        var cache = MakeCache();
        for (int i = 0; i < SuggestionAnchorCache.Capacity + 4; i++) cache.Record("1", $"prefix {i}", $"suffix {i}");
        Assert.Null(cache.Remainder("1", "prefix 0"));
        int last = SuggestionAnchorCache.Capacity + 3;
        Assert.Equal($"suffix {last}", cache.Remainder("1", $"prefix {last}"));
    }

    [Fact]
    public void LongPrefixesMatchOnTheBoundedTail()
    {
        var cache = MakeCache();
        string longPrefix = new string('a', 2000) + " ending here";
        cache.Record("1", longPrefix, " and more");
        Assert.Equal(" more", cache.Remainder("1", longPrefix + " and"));
    }

    [Fact]
    public void EmptySuggestionIsNeverRecorded()
    {
        var cache = MakeCache();
        cache.Record("1", "Hello", "");
        Assert.Null(cache.Remainder("1", "Hello"));
    }

    [Fact]
    public void ReRecordingAnIdenticalAnchorRefreshesItsExpiry()
    {
        var cache = MakeCache();
        cache.Record("1", "Hello", " world");
        _clock.Now += TimeSpan.FromSeconds(100);
        cache.Record("1", "Hello", " world");
        _clock.Now += TimeSpan.FromSeconds(100);
        Assert.Equal(" world", cache.Remainder("1", "Hello"));
    }

    [Fact]
    public void EntryExactlyAtMaxAgeIsStillLive()
    {
        var cache = MakeCache();
        cache.Record("1", "Hello", " world");
        _clock.Now += SuggestionAnchorCache.MaxEntryAge;
        Assert.Equal(" world", cache.Remainder("1", "Hello"));
    }

    [Fact]
    public void EqualConsumedDepthPrefersTheNewestAnchor()
    {
        var cache = MakeCache();
        cache.Record("1", "Hi", " there");
        _clock.Now += TimeSpan.FromSeconds(1);
        cache.Record("1", "Hi", " there friend");
        Assert.Equal("ere friend", cache.Remainder("1", "Hi th"));
    }

    [Fact]
    public void ClearForgetsEverything()
    {
        var cache = MakeCache();
        cache.Record("1", "Hello", " world");
        cache.Clear();
        Assert.Null(cache.Remainder("1", "Hello"));
    }
}
