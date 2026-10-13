using System.Globalization;
using System.Text;
using Cotabby.Core.Emoji;

namespace Cotabby.Core.Tests.Emoji;

public class EmojiCatalogTests
{
    private static EmojiCatalog FromJson(string json) =>
        EmojiCatalog.FromJson(new MemoryStream(Encoding.UTF8.GetBytes(json)));

    [Fact]
    public void FromJson_LoadsEntriesAndIgnoresExtraDatasetKeys()
    {
        var catalog = FromJson("""
            [
                {"glyph": "😀", "name": "grinning face", "aliases": ["grinning"],
                 "keywords": ["smile", "happy"], "group": "Smileys & Emotion", "unicodeVersion": "6.1"},
                {"glyph": "👍", "name": "thumbs up", "aliases": ["+1", "thumbsup"],
                 "keywords": ["approve"], "group": "People & Body", "unicodeVersion": "6.0"}
            ]
            """);

        Assert.Equal(["😀", "👍"], catalog.Indexed.Select(indexed => indexed.Entry.Glyph));
        Assert.Equal("😀", catalog.EntryForAlias("Grinning")?.Glyph);
        Assert.Equal("👍", catalog.EntryForAlias("+1")?.Glyph);
    }

    [Fact]
    public void FromJson_ReturnsEmptyCatalogWhenResourceIsMissing() =>
        Assert.Empty(EmojiCatalog.FromJson(null).Indexed);

    [Fact]
    public void FromJson_ReturnsEmptyCatalogWhenJsonIsMalformed() =>
        Assert.Empty(FromJson("this is not json").Indexed);

    [Fact]
    public void Index_PrecomputesLowercasedSearchTokensInInputOrder()
    {
        var entry = new EmojiEntry("🐱", "Cat Face", ["Cat", "kitty"], ["Pet"]);

        var indexed = Assert.Single(new EmojiCatalog([entry]).Indexed);

        Assert.Same(entry, indexed.Entry);
        Assert.Equal(["cat", "kitty"], indexed.LowerAliases);
        Assert.Equal(["pet"], indexed.LowerKeywords);
        Assert.Equal("cat face", indexed.LowerName);
    }

    [Fact]
    public void EntryForAlias_FirstOccurrenceWinsAndUnknownIsNull()
    {
        var catalog = new EmojiCatalog(
        [
            new EmojiEntry("😺", "smiley cat", ["cat"], []),
            new EmojiEntry("🐱", "cat face", ["CAT", "kitty"], []),
        ]);

        Assert.Equal("😺", catalog.EntryForAlias("Cat")?.Glyph);
        Assert.Equal("🐱", catalog.EntryForAlias("kitty")?.Glyph);
        Assert.Null(catalog.EntryForAlias("dog"));
        Assert.Null(new EmojiCatalog([]).EntryForAlias("cat"));
    }

    [Fact]
    public void Bundled_CoversUnicode16AndEveryEntryHasAnAlias()
    {
        var catalog = EmojiCatalog.Bundled;

        Assert.All(catalog.Indexed, indexed => Assert.NotEmpty(indexed.Entry.Aliases));
        // U+1FAE9 FACE WITH BAGS UNDER EYES arrived in Unicode 16.
        Assert.Contains(catalog.Indexed, indexed => indexed.Entry.Glyph == "\U0001FAE9");
    }

    [Fact]
    public void Bundled_SearchTokensAreOneUtf16UnitPerGrapheme()
    {
        // The matcher ranks and fuzzy matches by UTF-16 length, which is only the character count
        // while no search token holds a surrogate pair or a combining sequence.
        foreach (var indexed in EmojiCatalog.Bundled.Indexed)
        {
            foreach (var token in indexed.LowerAliases.Concat(indexed.LowerKeywords).Append(indexed.LowerName))
            {
                Assert.Equal(new StringInfo(token).LengthInTextElements, token.Length);
            }
        }
    }
}
