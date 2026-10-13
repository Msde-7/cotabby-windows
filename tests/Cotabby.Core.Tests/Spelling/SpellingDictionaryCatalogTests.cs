using Cotabby.Core.Spelling;

namespace Cotabby.Core.Tests.Spelling;

public class SpellingDictionaryCatalogTests
{
    [Fact]
    public void Normalize_TrimsLowercasesDropsUnknownAndDuplicatesInCatalogOrder()
    {
        Assert.Equal(["en", "de", "ru"], SpellingDictionaryCatalog.Normalize([" RU ", "xx", "De", "ru", "EN"]));
    }

    [Fact]
    public void Normalize_EmptyInputStaysEmpty()
    {
        // An explicitly empty selection must not fall back to English.
        Assert.Empty(SpellingDictionaryCatalog.Normalize([]));
    }

    [Fact]
    public void Languages_MapsNormalizedCodesToCases()
    {
        Assert.Equal(
            [SpellingDictionaryLanguage.French, SpellingDictionaryLanguage.Hebrew],
            SpellingDictionaryCatalog.Languages(["he", "FR", "bogus"]));
        Assert.Equal(["en"], SpellingDictionaryCatalog.DefaultEnabledCodes);
    }

    [Fact]
    public void Codes_FollowStableCatalogOrder()
    {
        // Normalize emits codes in this order, so it is a persistence and rendering contract.
        Assert.Equal(
            ["en", "de", "es", "fr", "he", "it", "ru"],
            SpellingDictionaryCatalog.AllLanguages.Select(language => language.Code()));
    }

    [Fact]
    public void SettingsLabel_IncludesEnglishNameForEveryLanguageAndStaysUnique()
    {
        var labels = SpellingDictionaryCatalog.AllLanguages.Select(language => language.SettingsLabel()).ToArray();
        Assert.Equal(labels.Length, labels.Distinct().Count());
        Assert.All(
            SpellingDictionaryCatalog.AllLanguages,
            language => Assert.Contains(language.DisplayName(), language.SettingsLabel()));

        Assert.Equal("English", SpellingDictionaryLanguage.English.SettingsLabel());
        Assert.Equal("Deutsch (German)", SpellingDictionaryLanguage.German.SettingsLabel());
        Assert.Equal("עברית (Hebrew)", SpellingDictionaryLanguage.Hebrew.SettingsLabel());
    }

    [Fact]
    public void EveryCatalogDictionaryShipsNextToTheAssemblyAndParses()
    {
        var load = SpellingDictionaryResources.Loader();
        foreach (var language in SpellingDictionaryCatalog.AllLanguages)
        {
            var contents = load(language);
            Assert.True(contents is not null, $"Missing bundled {language.DisplayName()} dictionary");

            var columns = contents.Split('\n', 2)[0].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(2, columns.Length);
            Assert.True(long.TryParse(columns[1], out _));

            // Lookups compose typed text to NFC and compare ordinally, so the files must be NFC with LF endings.
            Assert.True(contents.IsNormalized(), $"{language.DisplayName()} dictionary is not NFC");
            Assert.DoesNotContain('\r', contents);
        }
    }

    [Fact]
    public void Loader_ReturnsNullForAMissingDirectory()
    {
        var load = SpellingDictionaryResources.Loader(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));
        Assert.Null(load(SpellingDictionaryLanguage.English));
    }

    [Fact]
    public void NoticeAndLicensesShipWithTheDictionaries()
    {
        var directory = SpellingDictionaryResources.DefaultDirectory;
        Assert.True(File.Exists(Path.Combine(directory, "NOTICE.md")));
        Assert.Equal(10, Directory.GetFiles(Path.Combine(directory, "Licenses")).Length);
    }
}
