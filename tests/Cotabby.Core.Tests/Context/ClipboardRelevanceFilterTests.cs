using Cotabby.Core.Context;

namespace Cotabby.Core.Tests.Context;

public sealed class ClipboardRelevanceFilterTests
{
    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private readonly ManualClock _clock = new();
    private readonly ClipboardRelevanceFilter _filter;

    public ClipboardRelevanceFilterTests() => _filter = new ClipboardRelevanceFilter(_clock);

    private void EstablishBaseline(uint sequenceNumber = 1) =>
        Assert.Null(_filter.Filter("baseline", sequenceNumber, ""));

    [Fact]
    public void NullClipboard_ReturnsNull()
    {
        EstablishBaseline();

        Assert.Null(_filter.Filter(null, 2, "hello world"));
    }

    [Fact]
    public void FirstObservation_ReturnsNullEvenWithOverlap()
    {
        Assert.Null(_filter.Filter("meeting agenda", 42, "the meeting starts soon"));
    }

    [Fact]
    public void NullClipboard_DoesNotRecordTheBaseline()
    {
        Assert.Null(_filter.Filter(null, 1, ""));

        Assert.Null(_filter.Filter("meeting agenda", 2, "the meeting starts soon"));
    }

    [Fact]
    public void UnchangedSequenceAfterBaseline_ReturnsNull()
    {
        EstablishBaseline(42);

        Assert.Null(_filter.Filter("meeting agenda", 42, "the meeting starts soon"));
    }

    [Fact]
    public void FirstChangeAfterBaseline_ReturnsContentWhenOverlapMatches()
    {
        EstablishBaseline(42);

        Assert.Equal(
            "meeting agenda for Thursday",
            _filter.Filter("meeting agenda for Thursday", 43, "Let's discuss the meeting"));
    }

    [Theory]
    [InlineData("Deployment Pipeline", "the deployment is running", "Deployment Pipeline")]
    [InlineData("SELECT * FROM users", "Dear hiring manager", null)]
    [InlineData("a b c", "a b c d e", null)]
    public void Overlap_IgnoresCaseAndTokensShorterThanThree(string clipboard, string prefix, string? expected)
    {
        EstablishBaseline();

        Assert.Equal(expected, _filter.Filter(clipboard, 2, prefix));
    }

    [Fact]
    public void Staleness_ExpiresAtTheThresholdFromTheCopy()
    {
        EstablishBaseline();
        Assert.Equal("fresh content", _filter.Filter("fresh content", 2, "fresh content"));

        _clock.Now += ClipboardRelevanceFilter.StaleThreshold - TimeSpan.FromSeconds(1);
        Assert.Equal("fresh content", _filter.Filter("fresh content", 2, "fresh content"));

        _clock.Now += TimeSpan.FromSeconds(1);
        Assert.Null(_filter.Filter("fresh content", 2, "fresh content"));
    }

    [Fact]
    public void NewCopy_ResetsTheStalenessClock()
    {
        EstablishBaseline();
        _filter.Filter("first content", 2, "first content");

        _clock.Now += ClipboardRelevanceFilter.StaleThreshold + TimeSpan.FromSeconds(1);

        Assert.Equal(
            "second content matching prefix",
            _filter.Filter("second content matching prefix", 3, "second content"));
    }
}
