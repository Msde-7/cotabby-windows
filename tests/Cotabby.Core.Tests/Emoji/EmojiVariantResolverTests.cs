using System.Text;
using Cotabby.Core.Emoji;

namespace Cotabby.Core.Tests.Emoji;

/// <summary>Glyphs are written as explicit scalars so modifier placement is unambiguous.</summary>
public class EmojiVariantResolverTests
{
    private const string Firefighter = "\U0001F9D1‍\U0001F692";
    private const string ManFirefighter = "\U0001F468‍\U0001F692";
    private const string WomanFirefighter = "\U0001F469‍\U0001F692";

    private static readonly EmojiMatch Wave = Match("\U0001F44B", "waving hand", "wave");

    private static EmojiMatch Match(string glyph, string name, string alias) =>
        new(new EmojiEntry(glyph, name, [alias], []));

    private static string[] Resolve(
        EmojiMatch[] matches,
        EmojiSkinTone skinTone = EmojiSkinTone.Neutral,
        EmojiGender gender = EmojiGender.Neutral) =>
        EmojiVariantResolver.Resolve(matches, new EmojiVariantPreferences(skinTone, gender))
            .Select(match => match.Glyph)
            .ToArray();

    [Fact]
    public void SkinTone_KeepsDefaultVariantAfterPreferredTone() =>
        Assert.Equal(["\U0001F44B\U0001F3FD", "\U0001F44B"], Resolve([Wave], EmojiSkinTone.Medium));

    [Fact]
    public void SkinTone_LeavesUnsupportedEmojiUntoned() =>
        Assert.Equal(["\U0001F436"], Resolve([Match("\U0001F436", "dog face", "dog")], EmojiSkinTone.Dark));

    [Fact]
    public void SkinTone_NeutralLeavesGlyphsUnchanged() =>
        Assert.Equal(["\U0001F44B"], Resolve([Wave]));

    [Fact]
    public void SkinTone_AppliesAfterTheLeadingPersonScalarInsideZwjSequence() =>
        Assert.Equal(
            ["\U0001F9D1\U0001F3FD‍\U0001F692", Firefighter],
            Resolve([Match(Firefighter, "firefighter", "firefighter")], EmojiSkinTone.Medium));

    [Fact]
    public void SkinTone_TonedCellKeepsTheSourceEntry()
    {
        var resolved = EmojiVariantResolver.Resolve(
            [Wave], new EmojiVariantPreferences(EmojiSkinTone.Light, EmojiGender.Neutral));

        Assert.All(resolved, match => Assert.Same(Wave.Entry, match.Entry));
        Assert.Equal("\U0001F44B\U0001F3FB", resolved[0].Glyph);
    }

    [Fact]
    public void TonedGlyph_RefusesAlreadyTonedOrUnsupportedGlyphs()
    {
        var medium = EmojiSkinTone.Medium.Modifier()!;

        Assert.Null(EmojiVariantResolver.TonedGlyph("\U0001F44B\U0001F3FF", medium));
        Assert.Null(EmojiVariantResolver.TonedGlyph("\U0001F436", medium));
        Assert.Null(EmojiVariantResolver.TonedGlyph("", medium));
    }

    [Fact]
    public void TonedGlyph_InsertsTheModifierBeforeAVariationSelector() =>
        // U+261D is a single UTF-16 unit, unlike the astral bases in the other cases.
        Assert.Equal(
            "☝\U0001F3FD️",
            EmojiVariantResolver.TonedGlyph("☝️", EmojiSkinTone.Medium.Modifier()!));

    [Fact]
    public void IsModifierBase_MatchesRangeEdges()
    {
        Assert.True(EmojiVariantResolver.IsModifierBase(new Rune(0x261D)));
        Assert.True(EmojiVariantResolver.IsModifierBase(new Rune(0x1FAF8)));
        Assert.False(EmojiVariantResolver.IsModifierBase(new Rune(0x1F3FB)));
        Assert.False(EmojiVariantResolver.IsModifierBase(new Rune('a')));
    }

    [Fact]
    public void Gender_PrefersConfiguredVariantAndCollapsesTheFamily()
    {
        EmojiMatch[] family =
        [
            Match(Firefighter, "firefighter", "firefighter"),
            Match(ManFirefighter, "man firefighter", "man_firefighter"),
            Match(WomanFirefighter, "woman firefighter", "woman_firefighter"),
        ];

        Assert.Equal([WomanFirefighter], Resolve(family, gender: EmojiGender.Female));
        Assert.Equal([ManFirefighter], Resolve(family, gender: EmojiGender.Male));
        Assert.Equal([Firefighter], Resolve(family));
    }

    [Fact]
    public void Gender_FallsBackToNeutralWhenPreferredVariantMissing()
    {
        EmojiMatch[] partial =
        [
            Match(Firefighter, "firefighter", "firefighter"),
            Match(ManFirefighter, "man firefighter", "man_firefighter"),
        ];

        Assert.Equal([Firefighter], Resolve(partial, gender: EmojiGender.Female));
    }

    [Fact]
    public void Gender_FallsBackToTheHighestRankedVariantWithoutANeutralOne()
    {
        EmojiMatch[] gendered =
        [
            Match(WomanFirefighter, "woman firefighter", "woman_firefighter"),
            Match(ManFirefighter, "man firefighter", "man_firefighter"),
        ];

        Assert.Equal([WomanFirefighter], Resolve(gendered));
    }

    [Fact]
    public void Gender_SingleGenderedVariantPassesThrough() =>
        // With one member of a family present there is nothing to collapse.
        Assert.Equal(
            [ManFirefighter],
            Resolve([Match(ManFirefighter, "man firefighter", "man_firefighter")], gender: EmojiGender.Female));

    [Fact]
    public void GenderIsResolvedBeforeSkinTone()
    {
        EmojiMatch[] family =
        [
            Match(Firefighter, "firefighter", "firefighter"),
            Match(WomanFirefighter, "woman firefighter", "woman_firefighter"),
        ];

        Assert.Equal(
            ["\U0001F469\U0001F3FF‍\U0001F692", WomanFirefighter],
            Resolve(family, EmojiSkinTone.Dark, EmojiGender.Female));
    }

    [Fact]
    public void Gender_LeavesNonGenderedEmojiUntouched()
    {
        EmojiMatch[] matches = [Match("\U0001F604", "smile", "smile"), Match("\U0001F436", "dog", "dog")];

        Assert.Equal(["\U0001F604", "\U0001F436"], Resolve(matches, gender: EmojiGender.Male));
    }
}
