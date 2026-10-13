using Cotabby.Core.Emoji;

namespace Cotabby.Core.Tests.Emoji;

/// <summary>
/// The padding window is <c>limit * 3</c> aliases of <see cref="EmojiPopularity.Ordered"/>, so the
/// expectations follow from list positions. joy is 0, heart is 1, rocket is 25 and unicorn is far
/// past the window.
/// </summary>
public class EmojiRecentsTests
{
    private static EmojiEntry Entry(string glyph, string alias) => new(glyph, alias, [alias], []);

    private static readonly EmojiCatalog SampleCatalog = new(
    [
        Entry("😀", "grinning"),
        Entry("😂", "joy"),
        Entry("❤️", "heart"),
        Entry("🚀", "rocket"),
        Entry("🦄", "unicorn"),
    ]);

    private static string[] Glyphs(int limit, params string[] recents) =>
        EmojiRecents.Suggestions(new EmojiUsageSnapshot(recents, new Dictionary<string, int>()), SampleCatalog, limit)
            .Select(match => match.Glyph)
            .ToArray();

    [Fact]
    public void RecentsLeadInOrderThenPopularityPads() =>
        Assert.Equal(["🦄", "😀", "😂", "❤️", "🚀"], Glyphs(10, "unicorn", "grinning"));

    [Fact]
    public void EmptyUsageUsesOnlyThePopularityWindow() =>
        Assert.Equal(["😂", "❤️", "🚀"], Glyphs(10));

    [Fact]
    public void PaddingWindowScalesWithLimit() =>
        Assert.Equal(["😂", "❤️"], Glyphs(5));

    [Fact]
    public void UnresolvableRecentAliasIsSkipped() =>
        Assert.Equal(["😂", "❤️"], Glyphs(5, "not_in_catalog", "joy"));

    [Fact]
    public void RecentThatIsAlsoPopularAppearsOnceAtItsRecentPosition() =>
        Assert.Equal(["❤️", "😂", "🚀"], Glyphs(10, "HEART"));

    [Fact]
    public void LimitIsRespected()
    {
        Assert.Equal(["😂", "❤️"], Glyphs(2));
        Assert.Equal(["🦄"], Glyphs(1, "unicorn", "grinning"));
    }

    [Fact]
    public void NonPositiveLimitReturnsNothing()
    {
        Assert.Empty(Glyphs(0));
        Assert.Empty(Glyphs(-1));
    }
}
