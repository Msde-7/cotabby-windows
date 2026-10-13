using Cotabby.Core.Spelling;
using static Cotabby.Core.Spelling.SpellingDictionaryLanguage;

namespace Cotabby.Core.Tests.Spelling;

/// <summary>
/// Scored cases read the bundled dictionaries and use clearly monolingual samples. The confidence
/// rule itself is tested through the pure ConfidentLanguage seam.
/// </summary>
public class SpellingLanguageResolverTests
{
    private readonly SpellingLanguageResolver _resolver = new();

    [Fact]
    public void EmptyEnabledSetReturnsNull()
    {
        Assert.Null(_resolver.Resolve("This is teh", "teh", []));
    }

    [Fact]
    public void SingleEnabledLanguageUsesExplicitSelection()
    {
        Assert.Equal(French, _resolver.Resolve("ambiguous text bonjoru", "bonjoru", [French]));
    }

    [Fact]
    public void MultilingualContextSelectsGerman()
    {
        Assert.Equal(
            German,
            _resolver.Resolve("Das ist ein kurzer deutscher Satz mit einem Feler", "Feler", [English, German, Spanish]));
    }

    [Fact]
    public void MultilingualContextSelectsSpanish()
    {
        Assert.Equal(
            Spanish,
            _resolver.Resolve("Este es un texto breve escrito en españl", "españl", [English, Spanish, French]));
    }

    [Theory]
    [InlineData("I think we should meet tomorrow and talk about teh", English)]
    [InlineData("Je pense que nous devons parler de cette choze", French)]
    [InlineData("Penso che dobbiamo parlare di questa coza", Italian)]
    [InlineData("Я думаю что нам нужно поговорить об этом сегодня", Russian)]
    [InlineData("אני חושב שאנחנו צריכים לדבר על זה היום", Hebrew)]
    public void EveryBundledLanguageIsRecognizedAmongAllOfThem(string text, SpellingDictionaryLanguage expected)
    {
        Assert.Equal(expected, _resolver.Resolve(text + " ", "", SpellingDictionaryCatalog.AllLanguages));
    }

    [Fact]
    public void ScriptDistinctWordCanResolveWithoutEarlierContext()
    {
        Assert.Equal(Russian, _resolver.Resolve("превет", "превет", [English, Russian]));
    }

    [Fact]
    public void UnknownLatinWordDoesNotPickAmongLatinLanguages()
    {
        Assert.Null(_resolver.Resolve("qwxzy", "qwxzy", [English, German]));
        // Text in a script that no enabled language uses carries no evidence.
        Assert.Null(_resolver.Resolve("γειά σου κόσμε ", "", [English, Russian]));
    }

    [Fact]
    public void LowConfidenceHypothesisIsRejected()
    {
        Assert.Null(SpellingLanguageResolver.ConfidentLanguage(
            new Dictionary<SpellingDictionaryLanguage, double> { [English] = 0.46, [Italian] = 0.24, [Spanish] = 0.18 }));
    }

    [Fact]
    public void ConfidenceThresholdIsInclusive()
    {
        Assert.Equal(German, SpellingLanguageResolver.ConfidentLanguage(
            new Dictionary<SpellingDictionaryLanguage, double> { [German] = 0.55, [English] = 0.45 }));
        Assert.Null(SpellingLanguageResolver.ConfidentLanguage(
            new Dictionary<SpellingDictionaryLanguage, double> { [German] = 0.549, [English] = 0.451 }));
    }

    [Fact]
    public void NoScoresMeansNoLanguage()
    {
        Assert.Null(SpellingLanguageResolver.ConfidentLanguage(new Dictionary<SpellingDictionaryLanguage, double>()));
    }

    [Fact]
    public void SingleEnabledLanguageWinsEvenWithoutAnyText()
    {
        Assert.Equal(Hebrew, _resolver.Resolve("", "", [Hebrew]));
    }

    [Fact]
    public void EmptyContextWithMultipleEnabledLanguagesReturnsNull()
    {
        Assert.Null(_resolver.Resolve("", "", [English, German]));
    }

    [Fact]
    public void CurrentWordNotAtEndOfContextStillResolvesFromFullContext()
    {
        Assert.Equal(
            German,
            _resolver.Resolve("Das ist ein kurzer deutscher Satz mit einem", "Feler", [English, German, Spanish]));
    }

    [Fact]
    public void WordsSharedBetweenLanguagesSplitTheirVote()
    {
        var resolver = new SpellingLanguageResolver(language => language switch
        {
            English => "common 50\nenglish 50",
            German => "common 50\ndeutsch 50",
            _ => null,
        });

        // The shared word is equally common in both, so alone it decides nothing.
        Assert.Null(resolver.Resolve("common ", "", [English, German]));
        Assert.Equal(German, resolver.Resolve("common deutsch ", "", [English, German]));
        Assert.Equal(English, resolver.Resolve("ENGLISH, common! ", "", [English, German]));
    }
}
