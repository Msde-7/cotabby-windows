using Cotabby.Core.Emoji;

namespace Cotabby.Core.Tests.Emoji;

public class EmojiPopularityTests
{
    [Fact]
    public void RankIsTheListIndex()
    {
        Assert.Equal(0, EmojiPopularity.Rank("joy"));
        Assert.Equal(1, EmojiPopularity.Rank("heart"));
        for (int index = 0; index < EmojiPopularity.Ordered.Count; index++)
        {
            Assert.Equal(index, EmojiPopularity.Rank(EmojiPopularity.Ordered[index]));
        }
    }

    [Fact]
    public void OrderedListHasNoDuplicatesAndIsLowercase()
    {
        // A duplicate would be shadowed by its first occurrence and a mixed case entry would never
        // match the lowercased aliases the matcher ranks with.
        Assert.Equal(EmojiPopularity.Ordered.Count, EmojiPopularity.Ordered.Distinct().Count());
        Assert.All(EmojiPopularity.Ordered, alias => Assert.Equal(alias.ToLowerInvariant(), alias));
    }

    [Fact]
    public void AbsentAliasIsNotRanked() =>
        Assert.Equal(EmojiPopularity.NotRanked, EmojiPopularity.Rank("definitely_not_an_emoji_alias"));

    [Fact]
    public void RankIsCaseInsensitive() =>
        Assert.Equal(EmojiPopularity.Rank("joy"), EmojiPopularity.Rank("JOY"));

    [Fact]
    public void StarterAliasesReturnsPrefixInOrder() =>
        Assert.Equal(EmojiPopularity.Ordered.Take(3), EmojiPopularity.StarterAliases(3));

    [Fact]
    public void StarterAliasesLimitBeyondListReturnsWholeList() =>
        Assert.Equal(EmojiPopularity.Ordered, EmojiPopularity.StarterAliases(EmojiPopularity.Ordered.Count + 10));

    [Fact]
    public void StarterAliasesClampsToZero()
    {
        Assert.Empty(EmojiPopularity.StarterAliases(0));
        Assert.Empty(EmojiPopularity.StarterAliases(-5));
    }
}
