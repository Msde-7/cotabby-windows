using Cotabby.Core.Support;

namespace Cotabby.Core.Tests;

public class RepetitionGuardTests
{
    [Fact]
    public void BlocksTokenThatWouldRepeatTrigram()
    {
        // The pair (1, 2) was previously continued with 3.
        Assert.Contains(3, RepetitionGuard.BlockedTokens([1, 2, 3, 1, 2]));
    }

    [Fact]
    public void NoBlocksOnShortHistory()
    {
        Assert.Empty(RepetitionGuard.BlockedTokens([7]));
    }

    [Fact]
    public void DifferentPrefixNotBlocked()
    {
        Assert.Empty(RepetitionGuard.BlockedTokens([1, 2, 3, 4, 5]));
    }
}
