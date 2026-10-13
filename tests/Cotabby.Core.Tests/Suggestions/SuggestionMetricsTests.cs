using Cotabby.Core.Suggestions;

namespace Cotabby.Core.Tests.Suggestions;

public sealed class SuggestionMetricsTests
{
    private readonly SuggestionMetrics _metrics = new();

    [Fact]
    public void RecordShown_CountsTheSuggestionAndKeepsItsLatency()
    {
        _metrics.RecordShown(40);
        _metrics.RecordShown(60);

        Assert.Equal(2, _metrics.SuggestionsShown);
        Assert.Equal([40, 60], _metrics.LatenciesMs);
    }

    [Fact]
    public void RecordShown_KeepsOnlyTheMostRecentLatencies()
    {
        for (int latency = 1; latency <= 125; latency++) _metrics.RecordShown(latency);

        Assert.Equal(125, _metrics.SuggestionsShown);
        Assert.Equal(Enumerable.Range(6, 120), _metrics.LatenciesMs);
    }

    [Fact]
    public void RecordAccepted_CountsASuggestionOncePerFirstChunkAndEveryWord()
    {
        _metrics.RecordAccepted(" hello", firstChunkOfSuggestion: true);
        _metrics.RecordAccepted(" big world", firstChunkOfSuggestion: false);

        Assert.Equal(1, _metrics.SuggestionsAccepted);
        Assert.Equal(3, _metrics.WordsAccepted);
    }

    [Fact]
    public void RecordAccepted_PunctuationOnlyChunksAddNoWords()
    {
        _metrics.RecordAccepted(" ...", firstChunkOfSuggestion: true);

        Assert.Equal(1, _metrics.SuggestionsAccepted);
        Assert.Equal(0, _metrics.WordsAccepted);
    }

    [Fact]
    public void Restore_SeedsTheWordCountThatLaterAcceptsAddTo()
    {
        _metrics.Restore(100);
        _metrics.RecordAccepted(" hello world", firstChunkOfSuggestion: true);

        Assert.Equal(102, _metrics.WordsAccepted);
    }

    [Fact]
    public void Changed_FiresForEveryRecordedEvent()
    {
        int changes = 0;
        _metrics.Changed += () => changes++;

        _metrics.RecordShown(40);
        _metrics.RecordAccepted(" hello", firstChunkOfSuggestion: true);

        Assert.Equal(2, changes);
    }
}
