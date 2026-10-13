using Cotabby.Core.Emoji;

namespace Cotabby.Core.Tests.Emoji;

public class EmojiSynonymCatalogTests
{
    [Fact]
    public void ExactKeyBoostsExactlyItsMappedAliases()
    {
        var boosted = EmojiSynonymCatalog.BoostedAliases("lol");

        Assert.Equal(["joy", "rofl"], boosted.Exact.Order());
        // "lol" is not a proper prefix of any other key.
        Assert.Empty(boosted.Prefix);
    }

    [Fact]
    public void QueryIsTrimmedAndCaseFolded() =>
        Assert.Equal(["joy", "rofl"], EmojiSynonymCatalog.BoostedAliases("  LOL ").Exact.Order());

    [Fact]
    public void MapKeysAndValuesAreLowercase()
    {
        // The query and the aliases are compared lowercased, so a mixed case entry would be unreachable.
        foreach (var (key, aliases) in EmojiSynonymCatalog.Map)
        {
            Assert.Equal(key.ToLowerInvariant(), key);
            Assert.All(aliases, alias => Assert.Equal(alias.ToLowerInvariant(), alias));
        }
    }

    [Fact]
    public void PrefixKeyBoostsViaPrefix()
    {
        // "lo" is a prefix of "lol" (joy) and "love" (heart).
        var boosted = EmojiSynonymCatalog.BoostedAliases("lo");

        Assert.Contains("joy", boosted.Prefix);
        Assert.Contains("heart", boosted.Prefix);
    }

    [Fact]
    public void PrefixExcludesExact()
    {
        var boosted = EmojiSynonymCatalog.BoostedAliases("love");

        Assert.Contains("heart", boosted.Exact);
        Assert.False(boosted.Exact.Overlaps(boosted.Prefix));
    }

    [Fact]
    public void SingleCharacterDoesNotPrefixBoost() =>
        Assert.Empty(EmojiSynonymCatalog.BoostedAliases("l").Prefix);

    [Fact]
    public void BlankQueryBoostsNothing()
    {
        var boosted = EmojiSynonymCatalog.BoostedAliases("   ");

        Assert.Empty(boosted.Exact);
        Assert.Empty(boosted.Prefix);
    }
}
