using Cotabby.Win32.Focus;

namespace Cotabby.Inference.Tests;

public class PrecedingTextReadsTests
{
    private static readonly TimeSpan Slow = TimeSpan.FromMilliseconds(20);
    private static readonly TimeSpan Fast = TimeSpan.FromMilliseconds(2);

    [Fact]
    public void AShortDocument_IsAlwaysReadInFull()
    {
        var reads = new PrecedingTextReads();
        Assert.False(reads.UseBounded("f"));
        reads.FullRead("f", 5000, Slow);
        Assert.False(reads.UseBounded("f"));
    }

    [Fact]
    public void ALongDocument_IsReadBoundedNextTime()
    {
        var reads = new PrecedingTextReads();
        reads.FullRead("f", 500_000, Slow);
        Assert.True(reads.UseBounded("f"));
        Assert.False(reads.UseBounded("other field"));
        reads.BoundedRead("f", sufficed: true, Fast);
        Assert.True(reads.UseBounded("f"));
    }

    [Fact]
    public void ABoundedReadThatFailsOrIsSlower_GoesBackToFullReads()
    {
        var reads = new PrecedingTextReads();
        reads.FullRead("f", 500_000, Fast);
        reads.BoundedRead("f", sufficed: true, Slow);
        Assert.False(reads.UseBounded("f"));

        reads.FullRead("g", 500_000, Slow);
        Assert.True(reads.UseBounded("g"));
        reads.BoundedRead("g", sufficed: false, Fast);
        Assert.False(reads.UseBounded("g"));
        // Another long read of the same field does not try again.
        reads.FullRead("g", 500_000, Slow);
        Assert.False(reads.UseBounded("g"));
    }

    [Fact]
    public void KeepEnd_CutsToTheLimitWithoutSplittingASurrogatePair()
    {
        Assert.Equal("short", PrecedingTextReads.KeepEnd("short"));
        string text = "x" + new string('a', PrecedingTextReads.Kept);
        Assert.Equal(new string('a', PrecedingTextReads.Kept), PrecedingTextReads.KeepEnd(text));
        string split = "😀" + new string('a', PrecedingTextReads.Kept - 1);
        Assert.Equal(new string('a', PrecedingTextReads.Kept - 1), PrecedingTextReads.KeepEnd(split));
    }

    [Theory]
    [InlineData(100)]
    [InlineData(PrecedingTextReads.Kept - 1)]
    [InlineData(PrecedingTextReads.Kept)]
    [InlineData(PrecedingTextReads.Kept + 1)]
    [InlineData(40_000)]
    public void ABoundedRead_GivesWhatAFullReadGives(int boundedLength)
    {
        // A document of emoji and letters, so cuts can land inside a surrogate pair.
        var random = new Random(boundedLength);
        var document = string.Concat(Enumerable.Range(0, 30_000).Select(_ => random.Next(4) == 0 ? "😀" : "ab"));
        string full = PrecedingTextReads.KeepEnd(document);

        string bounded = document[^boundedLength..];
        if (char.IsLowSurrogate(bounded[0])) bounded = bounded[1..];
        string? fromBounded = PrecedingTextReads.FromBounded(bounded, startsAtDocumentStart: false);
        if (fromBounded is not null) Assert.Equal(full, fromBounded);
        else Assert.True(bounded.Length <= PrecedingTextReads.Kept);

        Assert.Equal(full, PrecedingTextReads.FromBounded(document, startsAtDocumentStart: true));
    }
}
