using System.Diagnostics;
using Cotabby.Core.Spelling;
using Xunit.Abstractions;
using static Cotabby.Core.Spelling.SpellingDictionaryLanguage;

namespace Cotabby.Core.Tests.Spelling;

public class CurrentWordSpellCheckerTests
{
    private sealed class StubLexicon(params string[] words) : ISpellingLexicon
    {
        public bool IsKnownWord(string word, SpellingDictionaryLanguage language) => words.Contains(word);
    }

    private readonly CurrentWordSpellChecker _checker = new(new StubLexicon("hello", "résumé"));

    [Fact]
    public void EmptyWordIsNeverATypo()
    {
        Assert.False(_checker.IsTypo("", English));
    }

    [Theory]
    [InlineData("hello", false)]
    [InlineData("helo", true)]
    [InlineData("Teh", true)]
    [InlineData("qqqqzzzzqq", true)]
    // Only a token that is one whole word can be a typo.
    [InlineData("nmae,", false)]
    [InlineData("ok nmae", false)]
    [InlineData("I'm", false)]
    [InlineData("well-knwon", false)]
    // A decomposed accent is part of its letter, so the token is still one whole word.
    [InlineData("re\u0301sume\u0301", true)]
    public void OnlyAWholeUnknownWordIsATypo(string word, bool expected)
    {
        Assert.Equal(expected, _checker.IsTypo(word, English));
    }
}

/// <summary>Loads the bundled English dictionary once for the tests that need the real word list.</summary>
public sealed class EnglishDictionaryFixture : IDisposable
{
    public EnglishDictionaryFixture()
    {
        var stopwatch = Stopwatch.StartNew();
        Corrector = new SymSpellCorrector();
        // A cold dictionary vouches for every word, so the load is done once a nonsense word is unknown.
        Loaded = SpinWait.SpinUntil(() => !Corrector.IsKnownWord("qqzzxxqq", English), TimeSpan.FromSeconds(60));
        LoadTime = stopwatch.Elapsed;
    }

    public SymSpellCorrector Corrector { get; }

    public bool Loaded { get; }

    public TimeSpan LoadTime { get; }

    public void Dispose() => Corrector.Dispose();
}

public class TypoCorrectionAdvisorTests(EnglishDictionaryFixture english, ITestOutputHelper output)
    : IClassFixture<EnglishDictionaryFixture>
{
    private static readonly TypoGateSettings Offer = new(true, true, false);
    private static readonly TypoGateSettings Automatic = new(true, false, true);

    private readonly TypoCorrectionAdvisor _advisor = new(english.Corrector, new SpellingLanguageResolver());

    private TypoAdvice Advise(string precedingText, TypoGateSettings? settings = null, params string[] codes) =>
        _advisor.Advise(precedingText, codes.Length == 0 ? ["en"] : codes, settings ?? Offer);

    [Fact]
    public void BundledEnglishDictionaryLoadsFromTheOutputDirectory()
    {
        Assert.True(english.Loaded, "the English dictionary never loaded");
        output.WriteLine($"English dictionary ready after {english.LoadTime.TotalMilliseconds:F0} ms");
        Assert.False(english.Corrector.IsKnownWord("recieve", English));
        Assert.True(english.Corrector.IsKnownWord("receive", English));
    }

    [Theory]
    [InlineData("teh", "the")]
    [InlineData("recieve", "receive")]
    [InlineData("becuase", "because")]
    [InlineData("definately", "definitely")]
    [InlineData("seperate", "separate")]
    [InlineData("occured", "occurred")]
    [InlineData("tommorow", "tomorrow")]
    [InlineData("Freind", "Friend")]
    public void CorrectsCommonEnglishTypos(string typo, string expected)
    {
        var advice = Advise("I wrote " + typo + " ");

        Assert.Equal(TypoGateDecision.OfferCorrection(typo, expected), advice.Decision);
        Assert.Equal(new TypoCorrectionReplacement(typo.Length + 1, expected + " "), advice.Replacement);
    }

    [Fact]
    public void WellSpelledAndUnfinishedWordsProceed()
    {
        Assert.Equal(new TypoAdvice(TypoGateDecision.Proceed), Advise("I wrote the "));
        Assert.Equal(new TypoAdvice(TypoGateDecision.Proceed), Advise("I wrote The, "));
        // No delimiter yet, so the word may still be in progress.
        Assert.Equal(new TypoAdvice(TypoGateDecision.Proceed), Advise("I wrote teh"));
        // Contractions and hyphenated words are never judged as a whole.
        Assert.Equal(new TypoAdvice(TypoGateDecision.Proceed), Advise("I didnt't "));
        Assert.Equal(new TypoAdvice(TypoGateDecision.Proceed), Advise("a well-knwon "));
    }

    [Fact]
    public void TypoWithoutACorrectionSuppresses()
    {
        Assert.Equal(new TypoAdvice(TypoGateDecision.Suppress), Advise("I wrote qwxzyqq "));
        Assert.Equal(new TypoAdvice(TypoGateDecision.Suppress), Advise("I wrote teh ", new TypoGateSettings(true, false, false)));
    }

    [Fact]
    public void DisabledGateProceedsOnATypo()
    {
        Assert.Equal(new TypoAdvice(TypoGateDecision.Proceed), Advise("I wrote teh ", new TypoGateSettings(false, true, true)));
    }

    [Fact]
    public void AutomaticFixAppliesAfterASpaceAndKeepsTheReplacementRange()
    {
        var advice = Advise("I wrote teh ", Automatic);

        Assert.Equal(TypoGateDecision.ApplyCorrection("teh", "the"), advice.Decision);
        Assert.Equal(new TypoCorrectionReplacement(4, "the "), advice.Replacement);
    }

    [Fact]
    public void PunctuationDelimiterIsOfferedWithItsDelimiterAndNeverApplied()
    {
        var advice = Advise("I wrote teh, ", new TypoGateSettings(true, true, true));

        Assert.Equal(TypoGateDecision.OfferCorrection("teh", "the"), advice.Decision);
        Assert.Equal(new TypoCorrectionReplacement(5, "the, "), advice.Replacement);
        Assert.Equal(new TypoAdvice(TypoGateDecision.Suppress), Advise("I wrote teh, ", Automatic));
    }

    [Fact]
    public void NoEnabledDictionaryProceeds()
    {
        Assert.Equal(new TypoAdvice(TypoGateDecision.Proceed), _advisor.Advise("I wrote teh ", [], Offer));
        Assert.Equal(new TypoAdvice(TypoGateDecision.Proceed), _advisor.Advise("I wrote teh ", ["xx"], Offer));
    }

    [Fact]
    public void ColdDictionaryProceedsUntilItIsLoaded()
    {
        using var corrector = TestCorrector.Cold((English, "the 1000"));
        var advisor = new TypoCorrectionAdvisor(corrector, new SpellingLanguageResolver());
        Assert.Equal(new TypoAdvice(TypoGateDecision.Proceed), advisor.Advise("I wrote teh ", ["en"], Offer));

        TestCorrector.Load(corrector, English);
        Assert.Equal(TypoGateDecision.OfferCorrection("teh", "the"), advisor.Advise("I wrote teh ", ["en"], Offer).Decision);
    }

    [Fact]
    public void SeveralDictionariesUseTheLanguageOfTheSurroundingText()
    {
        // English context picks the English dictionary even though German is enabled too.
        var advice = Advise("I think that we should talk about teh ", Offer, "de", "en");
        Assert.Equal(TypoGateDecision.OfferCorrection("teh", "the"), advice.Decision);

        // Text that fits neither language leaves the word alone.
        Assert.Equal(new TypoAdvice(TypoGateDecision.Proceed), Advise("qwxzy teh ", Offer, "de", "en"));
    }

    [Fact]
    public void InjectedLexiconDecidesDetection()
    {
        var advisor = new TypoCorrectionAdvisor(english.Corrector, new SpellingLanguageResolver(), new EverythingIsKnown());
        Assert.Equal(new TypoAdvice(TypoGateDecision.Proceed), advisor.Advise("I wrote teh ", ["en"], Offer));
    }

    private sealed class EverythingIsKnown : ISpellingLexicon
    {
        public bool IsKnownWord(string word, SpellingDictionaryLanguage language) => true;
    }
}
