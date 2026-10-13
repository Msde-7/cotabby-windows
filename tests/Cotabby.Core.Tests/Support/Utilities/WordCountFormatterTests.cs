using Cotabby.Core.Support;

namespace Cotabby.Core.Tests;

public class WordCountFormatterTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(long.MinValue)]
    public void NonPositiveCountsHideTheBadge(long count)
    {
        Assert.Null(WordCountFormatter.CompactLabel(count));
    }

    [Theory]
    [InlineData(1, "1")]
    [InlineData(9, "9")]
    [InlineData(42, "42")]
    [InlineData(999, "999")]
    // One decimal of thousands, rounded half up, so the top of the tier reads "10.0K".
    [InlineData(1_000, "1.0K")]
    [InlineData(1_250, "1.3K")]
    [InlineData(1_500, "1.5K")]
    [InlineData(9_900, "9.9K")]
    [InlineData(9_949, "9.9K")]
    [InlineData(9_950, "10.0K")]
    [InlineData(9_951, "10.0K")]
    [InlineData(9_999, "10.0K")]
    // Whole thousands truncate rather than round.
    [InlineData(10_000, "10K")]
    [InlineData(10_999, "10K")]
    [InlineData(50_000, "50K")]
    [InlineData(999_999, "999K")]
    [InlineData(1_000_000, "1.0M")]
    [InlineData(5_500_000, "5.5M")]
    [InlineData(9_949_999, "9.9M")]
    [InlineData(9_950_000, "10.0M")]
    [InlineData(9_999_999, "10.0M")]
    // Whole millions truncate.
    [InlineData(10_000_000, "10M")]
    [InlineData(42_000_000, "42M")]
    [InlineData(999_999_999, "999M")]
    [InlineData(long.MaxValue, "9223372036854M")]
    public void CompactLabelTiers(long count, string label)
    {
        Assert.Equal(label, WordCountFormatter.CompactLabel(count));
    }
}
