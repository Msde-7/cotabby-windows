using Cotabby.Core.Emoji;

namespace Cotabby.Core.Tests.Emoji;

public class EmojiPickerModelsTests
{
    private static readonly EmojiEntry Wave = new("\U0001F44B", "waving hand", ["wave"], ["hello"]);

    [Fact]
    public void EmojiMatch_DefaultsGlyphToEntryGlyph() =>
        Assert.Equal("\U0001F44B", new EmojiMatch(Wave).Glyph);

    [Fact]
    public void EmojiMatch_VariantOverrideChangesIdentityButKeepsSourceEntry()
    {
        var toned = new EmojiMatch(Wave, "\U0001F44B\U0001F3FD");

        Assert.Equal("\U0001F44B\U0001F3FD", toned.Glyph);
        Assert.Same(Wave, toned.Entry);
        Assert.NotEqual(new EmojiMatch(Wave), toned);
    }

    [Fact]
    public void EmojiSkinTone_DisplayNamesArePinnedSettingsCopy()
    {
        Assert.Equal("Default", EmojiSkinTone.Neutral.DisplayName());
        Assert.Equal("Light", EmojiSkinTone.Light.DisplayName());
        Assert.Equal("Medium Light", EmojiSkinTone.MediumLight.DisplayName());
        Assert.Equal("Medium", EmojiSkinTone.Medium.DisplayName());
        Assert.Equal("Medium Dark", EmojiSkinTone.MediumDark.DisplayName());
        Assert.Equal("Dark", EmojiSkinTone.Dark.DisplayName());
    }

    [Fact]
    public void EmojiSkinTone_ModifiersAreTheFitzpatrickScalarsInLightToDarkOrder() =>
        Assert.Equal(
            [null, "\U0001F3FB", "\U0001F3FC", "\U0001F3FD", "\U0001F3FE", "\U0001F3FF"],
            Enum.GetValues<EmojiSkinTone>().Select(tone => tone.Modifier()));

    [Fact]
    public void EmojiSkinTone_SampleGlyphAppendsModifierAndNeutralKeepsVariationSelector()
    {
        Assert.Equal("✌️", EmojiSkinTone.Neutral.SampleGlyph());
        foreach (var tone in Enum.GetValues<EmojiSkinTone>().Where(tone => tone != EmojiSkinTone.Neutral))
        {
            Assert.Equal("✌" + tone.Modifier(), tone.SampleGlyph());
        }
    }

    [Fact]
    public void EmojiGender_DisplayNamesArePinnedSettingsCopy()
    {
        Assert.Equal("Person", EmojiGender.Neutral.DisplayName());
        Assert.Equal("Man", EmojiGender.Male.DisplayName());
        Assert.Equal("Woman", EmojiGender.Female.DisplayName());
    }

    [Fact]
    public void EmojiGender_SampleGlyphsAreTheBasePersonManWomanScalars()
    {
        Assert.Equal("\U0001F9D1", EmojiGender.Neutral.SampleGlyph());
        Assert.Equal("\U0001F468", EmojiGender.Male.SampleGlyph());
        Assert.Equal("\U0001F469", EmojiGender.Female.SampleGlyph());
    }
}
