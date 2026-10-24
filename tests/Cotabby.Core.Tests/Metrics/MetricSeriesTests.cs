using Cotabby.Core.Metrics;

namespace Cotabby.Core.Tests;

public class MetricSeriesTests
{
    [Fact]
    public void MissingReadingsSplitTheSeriesIntoSegments()
    {
        var segments = MetricSeries.Segments([null, 10, 20, null, null, 30, 40, null]);

        Assert.Equal(2, segments.Count);
        Assert.Equal([(1, 10.0), (2, 20.0)], segments[0]);
        Assert.Equal([(5, 30.0), (6, 40.0)], segments[1]);
    }

    [Fact]
    public void AllMissingHasNoSegments()
    {
        Assert.Empty(MetricSeries.Segments([null, null]));
        Assert.Empty(MetricSeries.Segments([]));
    }

    [Fact]
    public void ACompleteSeriesIsOneSegment() =>
        Assert.Single(MetricSeries.Segments([1, 2, 3]));

    [Fact]
    public void UpperBoundLeavesHeadroomAboveThePeakButNotBelowTheFloor()
    {
        Assert.Equal(100, MetricSeries.UpperBound([10, 40], 100));
        Assert.Equal(180, MetricSeries.UpperBound([150, 20], 100), 6);
        Assert.Equal(128, MetricSeries.UpperBound([], 128));
    }
}
