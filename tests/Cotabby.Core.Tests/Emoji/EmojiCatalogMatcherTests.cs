using Cotabby.Core.Emoji;

namespace Cotabby.Core.Tests.Emoji;

public class EmojiCatalogMatcherTests
{
    private static EmojiEntry Entry(string glyph, string name, string[] aliases, string[]? keywords = null) =>
        new(glyph, name, aliases, keywords ?? []);

    private static EmojiMatcher Matcher(params EmojiEntry[] entries) => new(new EmojiCatalog(entries));

    private static string[] Glyphs(IEnumerable<EmojiMatch> matches) => matches.Select(match => match.Glyph).ToArray();

    private static EmojiUsageSnapshot Usage(string[]? recents = null, Dictionary<string, int>? frequency = null) =>
        new(recents ?? [], frequency ?? []);

    [Fact]
    public void ExactAliasOutranksPrefix()
    {
        var sut = Matcher(
            Entry("😄", "smiley", ["smiley"]),
            Entry("🙂", "slight smile", ["smile"]));

        Assert.Equal("🙂", sut.Matches("smile")[0].Glyph);
    }

    [Fact]
    public void PrefixBeatsSubstring()
    {
        var sut = Matcher(
            Entry("🐻", "bear", ["bear"]),
            Entry("🌍", "earth", ["earth"]),
            Entry("👂", "ear", ["ear"]));

        Assert.Equal(["👂", "🌍", "🐻"], Glyphs(sut.Matches("ear")));
    }

    [Fact]
    public void ShorterMatchedTokenRanksFirstWithinTier()
    {
        var sut = Matcher(
            Entry("😄", "smiley", ["smiley"]),
            Entry("🙂", "smile", ["smile"]));

        Assert.Equal(["🙂", "😄"], Glyphs(sut.Matches("smil")));
    }

    [Fact]
    public void KeywordWidensRecallWhenAliasDoesNotMatch()
    {
        var sut = Matcher(Entry("🎉", "party popper", ["tada"], ["party", "celebrate"]));

        Assert.Equal("🎉", sut.Matches("party")[0].Glyph);
    }

    [Fact]
    public void AliasSubstringBeatsKeywordSubstring()
    {
        var sut = Matcher(
            Entry("🎨", "palette", ["palette"], ["smart"]),
            Entry("💓", "beating heart", ["heartbeat"]));

        Assert.Equal(["💓", "🎨"], Glyphs(sut.Matches("art")));
    }

    [Fact]
    public void NameMatchesWhenAliasAndKeywordsDoNot()
    {
        var sut = Matcher(Entry("🧭", "compass rose", ["compass"]));

        Assert.Equal("🧭", sut.Matches("rose")[0].Glyph);
    }

    [Fact]
    public void QueryIsTrimmedAndCaseFolded()
    {
        var sut = Matcher(Entry("🙂", "slight smile", ["smile"]));

        Assert.Equal(["🙂"], Glyphs(sut.Matches("  SMILE ")));
    }

    [Fact]
    public void ExactSynonymLeadsLiteralAliasPrefixInTheSameTier()
    {
        // "lol" is an alias prefix of "lollipop" and an exact synonym for "joy". Both are tier 1
        // and the synonym's zero token length puts the intended emoji first.
        var sut = Matcher(
            Entry("🍭", "lollipop", ["lollipop"]),
            Entry("😂", "face with tears of joy", ["joy"]));

        Assert.Equal(["😂", "🍭"], Glyphs(sut.Matches("lol")));
    }

    [Fact]
    public void FuzzyFallbackOnlyRunsWhenLexicalResultsAreSparse()
    {
        var sut = Matcher(
            Entry("🥳", "hapyness", ["hapyness"]),
            Entry("😀", "happy face", ["happy"]));

        Assert.Equal(["🥳"], Glyphs(sut.Matches("hapy", limit: 1)));
        Assert.Equal(["🥳", "😀"], Glyphs(sut.Matches("hapy", limit: 5)));
    }

    [Fact]
    public void FuzzyNeedsAtLeastThreeCharacters()
    {
        var sut = Matcher(Entry("😀", "happy face", ["happy"]));

        Assert.Empty(sut.Matches("hp"));
        Assert.Equal("😀", sut.Matches("hpy")[0].Glyph);
    }

    [Fact]
    public void FrequentUseAloneMarksAFavorite()
    {
        var sut = Matcher(
            Entry("🅰️", "alpha", ["alpha"]),
            Entry("🅱️", "alphabet", ["alphabet"]));

        var once = Usage(frequency: new() { ["alphabet"] = 1 });
        Assert.Equal("🅰️", sut.Matches("alph", once)[0].Glyph);

        var often = Usage(frequency: new() { ["alphabet"] = EmojiUsageSnapshot.FrequentThreshold });
        Assert.Equal("🅱️", sut.Matches("alph", often)[0].Glyph);
    }

    [Fact]
    public void FavoriteNeverJumpsAStrongerTier()
    {
        var sut = Matcher(
            Entry("🅰️", "alpha", ["alpha"]),
            Entry("🅱️", "alphabet", ["alphabet"]));

        Assert.Equal("🅰️", sut.Matches("alpha", Usage(recents: ["alphabet"]))[0].Glyph);
    }

    [Fact]
    public void CatalogOrderIsTheFinalTiebreak()
    {
        var sut = Matcher(
            Entry("1️⃣", "zork one", ["zorka"]),
            Entry("2️⃣", "zork two", ["zorkb"]));

        Assert.Equal(["1️⃣", "2️⃣"], Glyphs(sut.Matches("zork")));
    }

    [Fact]
    public void SynonymSurfacesIntentWordWithNoLexicalMatch()
    {
        var sut = Matcher(
            Entry("🎈", "balloon", ["balloon"]),
            Entry("😂", "face with tears of joy", ["joy"]));

        Assert.Equal("😂", sut.Matches("lol")[0].Glyph);
    }

    [Fact]
    public void FuzzyMatchesDroppedLetterTypo()
    {
        var sut = Matcher(Entry("😀", "happy face", ["happy"]));

        Assert.Equal("😀", sut.Matches("hapy")[0].Glyph);
    }

    [Fact]
    public void FuzzyMatchesTransposition()
    {
        var sut = Matcher(Entry("📥", "incoming", ["receive"]));

        Assert.Equal("📥", sut.Matches("recieve")[0].Glyph);
    }

    [Fact]
    public void ExactMatchStillBeatsFuzzy()
    {
        var sut = Matcher(
            Entry("😀", "happy face", ["happy"]),
            Entry("🅷", "hapy tag", ["hapy"]));

        Assert.Equal("🅷", sut.Matches("hapy")[0].Glyph);
    }

    [Fact]
    public void FavoriteFloatsAboveShorterTokenWithinTier()
    {
        var sut = Matcher(
            Entry("🅰️", "alpha", ["alpha"]),
            Entry("🅱️", "alphabet", ["alphabet"]));

        Assert.Equal("🅰️", sut.Matches("alph")[0].Glyph);
        Assert.Equal("🅱️", sut.Matches("alph", Usage(recents: ["alphabet"]))[0].Glyph);
    }

    [Fact]
    public void PopularityBreaksTiesAtEqualRelevance()
    {
        var sut = Matcher(
            Entry("🌿", "hedge", ["hedge"]),
            Entry("❤️", "heart", ["heart"]));

        Assert.Equal("❤️", sut.Matches("he")[0].Glyph);
    }

    [Fact]
    public void RecentsLeadBareColonSuggestions()
    {
        var sut = Matcher(
            Entry("😀", "grinning", ["grinning"]),
            Entry("😂", "joy", ["joy"]));

        Assert.Equal("😀", sut.Recents(Usage(recents: ["grinning"]))[0].Glyph);
    }

    [Fact]
    public void EmptyQueryReturnsNothing()
    {
        var sut = Matcher(Entry("😀", "grinning", ["grinning"]));

        Assert.Empty(sut.Matches(""));
        Assert.Empty(sut.Matches("   "));
    }

    [Fact]
    public void LimitIsRespected()
    {
        var sut = Matcher(Enumerable.Range(0, 50).Select(i => Entry($"E{i}", $"alpha {i}", [$"alpha{i}"])).ToArray());

        Assert.Equal(5, sut.Matches("alpha", limit: 5).Count);
        Assert.Empty(sut.Matches("alpha", limit: 0));
    }

    [Fact]
    public void NoMatchReturnsEmpty()
    {
        var sut = Matcher(Entry("😀", "grinning", ["grinning"]));

        Assert.Empty(sut.Matches("zzzznope"));
    }

    [Fact]
    public void BundledCatalogLoadsAndDecodes()
    {
        Assert.NotEmpty(EmojiCatalog.Bundled.Indexed);
        Assert.Equal("😀", new EmojiMatcher(EmojiCatalog.Bundled).Matches("grinning")[0].Glyph);
    }
}
