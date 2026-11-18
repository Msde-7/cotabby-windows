namespace Cotabby.Inference.Tests;

public class MemoryPrefixTests
{
    [Theory]
    // A cut that left exactly the prefix.
    [InlineData(10, 0, 9, 0, true)]
    // Memory that dropped more than the tail, such as a hybrid model's invalidated recurrent
    // state, does not hold the prefix even though it ends before the cut.
    [InlineData(10, 0, 5, 0, false)]
    [InlineData(10, -1, -1, 0, false)]
    // A memory that refused the cut still holds the old tail.
    [InlineData(10, 0, 14, 0, false)]
    // Without a sliding window the prefix must start at the first position.
    [InlineData(10, 3, 9, 0, false)]
    // A sliding-window cache may have evicted what the window no longer reaches, but no more.
    [InlineData(100, 60, 99, 40, true)]
    [InlineData(100, 61, 99, 40, false)]
    [InlineData(30, 0, 29, 40, true)]
    [InlineData(30, 1, 29, 40, false)]
    // An empty prefix needs an empty memory.
    [InlineData(0, -1, -1, 0, true)]
    [InlineData(0, 0, 3, 0, false)]
    public void HoldsExactly_AcceptsOnlyMemoryEndingAtTheCutAndReachingBackFarEnough(
        int length, int minPosition, int maxPosition, int slidingWindow, bool expected) =>
        Assert.Equal(expected, MemoryPrefix.HoldsExactly(length, minPosition, maxPosition, slidingWindow));
}
