using Cotabby.Core.Support;

namespace Cotabby.Core.Tests;

public class TokenCountEstimatorTests
{
    [Theory]
    [InlineData("", 0)]
    [InlineData("   \n\t ", 0)]
    // Punctuation alone is not a word.
    [InlineData("...", 0)]
    // Every word is at least one token.
    [InlineData("a", 1)]
    [InlineData("hi there", 2)]
    // 1.25 rounds down, while 1.5 and 2.5 round away from zero.
    [InlineData("abcde", 1)]
    [InlineData("abcdef", 2)]
    [InlineData("abcdefghij", 3)]
    [InlineData("internationalization", 5)]
    [InlineData("word word word word word", 5)]
    [InlineData("cant", 1)]
    // Punctuation splits a contraction and a dotted identifier.
    [InlineData("can't", 2)]
    [InlineData("foobarbaz", 2)]
    [InlineData("foo.bar.baz", 3)]
    [InlineData("func(x)", 2)]
    public void EstimateMatchesWordAwareHeuristic(string text, int expected)
    {
        Assert.Equal(expected, TokenCountEstimator.Estimate(text));
    }
}
