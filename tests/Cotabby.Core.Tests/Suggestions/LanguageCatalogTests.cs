using Cotabby.Core.Suggestions;

namespace Cotabby.Core.Tests.Suggestions;

public sealed class LanguageCatalogTests
{
    [Fact]
    public void Normalize_TrimsDropsEmptiesAndDeduplicatesIgnoringCase() =>
        Assert.Equal(
            ["German", "english"],
            LanguageCatalog.Normalize(["  German ", "", "  ", "english", "English", "GERMAN"]));

    [Fact]
    public void Normalize_TruncatesLongNames()
    {
        var name = new string('a', LanguageCatalog.MaxLanguageLength + 10);

        Assert.Equal(LanguageCatalog.MaxLanguageLength, Assert.Single(LanguageCatalog.Normalize([name])).Length);
    }

    [Fact]
    public void Normalize_CapsTheCount()
    {
        var many = Enumerable.Range(0, LanguageCatalog.MaxLanguages + 5).Select(i => $"lang{i}");

        Assert.Equal(LanguageCatalog.MaxLanguages, LanguageCatalog.Normalize(many).Count);
    }

    [Fact]
    public void CommonLanguages_HaveUniqueNames()
    {
        var names = LanguageCatalog.CommonLanguages.Select(o => o.Name.ToLowerInvariant()).ToList();

        Assert.Equal(names.Count, names.Distinct().Count());
    }

    [Theory]
    [InlineData(1.7, "German")]
    [InlineData(1.3, "english")]
    [InlineData(1.7, " german ", "German")]
    public void EffectiveTokensPerWord_ASingleKnownLanguageUsesItsOwnFactor(double expected, params string[] languages) =>
        Assert.Equal(expected, LanguageCatalog.EffectiveTokensPerWord(languages));

    [Theory]
    [InlineData]
    [InlineData("German", "Spanish")]
    [InlineData("Klingon")]
    public void EffectiveTokensPerWord_NoneSeveralOrUnknownLanguagesUseTheFallback(params string[] languages) =>
        Assert.Equal(LanguageCatalog.FallbackTokensPerWord, LanguageCatalog.EffectiveTokensPerWord(languages));
}
