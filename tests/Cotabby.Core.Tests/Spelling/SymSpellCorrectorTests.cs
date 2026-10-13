using Cotabby.Core.Spelling;
using static Cotabby.Core.Spelling.SpellingDictionaryLanguage;

namespace Cotabby.Core.Tests.Spelling;

public class SymSpellCorrectorTests
{
    private static readonly TimeSpan BackgroundLoadTimeout = TimeSpan.FromSeconds(60);

    private static SymSpellCorrector MakeLoadedCorrector() =>
        TestCorrector.Loaded((English, "the 1000000\nname 50000\nreceive 8000\nseparate 4000"));

    [Fact]
    public void ReturnsNullBeforeIndexIsReady()
    {
        using var corrector = TestCorrector.Cold();
        Assert.Null(corrector.BestCorrection("teh"));
    }

    [Fact]
    public void CorrectsMisspelledWord()
    {
        using var corrector = MakeLoadedCorrector();
        Assert.Equal("receive", corrector.BestCorrection("recieve"));
        Assert.Equal("separate", corrector.BestCorrection("seperate"));
    }

    [Fact]
    public void TransfersCapitalization()
    {
        using var corrector = MakeLoadedCorrector();
        Assert.Equal("The", corrector.BestCorrection("Teh"));
    }

    [Fact]
    public void ReturnsNullForCorrectWord()
    {
        using var corrector = MakeLoadedCorrector();
        Assert.Null(corrector.BestCorrection("name"));
        Assert.Null(corrector.BestCorrection("Name"));
    }

    [Fact]
    public void ReturnsNullForGibberish()
    {
        using var corrector = MakeLoadedCorrector();
        Assert.Null(corrector.BestCorrection("qwxzy"));
    }

    [Fact]
    public void KeepsLanguageIndexesSeparate()
    {
        using var corrector = TestCorrector.Loaded((English, "gift 1000\ngive 500"), (German, "gibt 1000\ngift 10"));

        Assert.Equal("gift", corrector.BestCorrection("gibt", English));
        Assert.Null(corrector.BestCorrection("gibt", German));
    }

    [Fact]
    public void EvictsLeastRecentlyUsedLanguageAtCacheLimit()
    {
        using var corrector = TestCorrector.Cold((English, "the 1000"), (German, "das 1000"), (Spanish, "hola 1000"));
        TestCorrector.Load(corrector, English);
        TestCorrector.Load(corrector, German);

        // Touch English after German so German becomes the least recently used entry.
        Assert.Null(corrector.BestCorrection("the", English));
        TestCorrector.Load(corrector, Spanish);

        Assert.False(corrector.IsKnownWord("nmae", English));
        // German was evicted, so it is cold again and vouches for every word.
        Assert.True(corrector.IsKnownWord("nmae", German));
    }

    [Fact]
    public void KnownWordsFailOpenWhileColdAndIgnoreCaseOnceLoaded()
    {
        using var corrector = TestCorrector.Cold((English, "name 50000"));
        Assert.True(corrector.IsKnownWord("nmae", English));

        TestCorrector.Load(corrector, English);

        Assert.False(corrector.IsKnownWord("nmae", English));
        Assert.True(corrector.IsKnownWord("Name", English));
        // German is still cold, so nothing can vouch that the word is wrong there.
        Assert.True(corrector.IsKnownWord("nmae", German));
    }

    [Fact]
    public void ColdLookupsShareOneBackgroundLoadThenServeCorrections()
    {
        int calls = 0;
        using var release = new ManualResetEventSlim();
        using var corrector = new SymSpellCorrector(resourceLoader: _ =>
        {
            Interlocked.Increment(ref calls);
            // Hold the load open so the second lookup arrives while it is in flight.
            release.Wait();
            return "bonjour 1000\nbonsoir 500";
        });

        Assert.Null(corrector.BestCorrection("bonjoor", French));
        Assert.Null(corrector.BestCorrection("bonjoor", French));
        release.Set();

        Assert.True(WaitUntil(() => corrector.BestCorrection("bonjoor", French) == "bonjour"), "load never published");
        Assert.Equal(1, calls);
    }

    [Fact]
    public void MissingDictionaryResourceFailsOpen()
    {
        using var consulted = new ManualResetEventSlim();
        using var corrector = new SymSpellCorrector(resourceLoader: language =>
        {
            Assert.Equal(Italian, language);
            consulted.Set();
            return null;
        });

        // The first lookup schedules the background load. No index is ready yet.
        Assert.Null(corrector.BestCorrection("ciaoo", Italian));
        Assert.True(consulted.Wait(BackgroundLoadTimeout));

        // A failed load publishes nothing, so lookups keep failing open.
        Assert.Null(corrector.BestCorrection("ciaoo", Italian));
        Assert.True(corrector.IsKnownWord("ciaoo", Italian));
    }

    [Fact]
    public void DisposeCancelsALoadInFlight()
    {
        using var reading = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        int loads = 0;
        var corrector = new SymSpellCorrector(resourceLoader: _ =>
        {
            if (Interlocked.Increment(ref loads) == 1)
            {
                reading.Set();
                release.Wait();
            }
            return "the 1000";
        });

        Assert.Null(corrector.BestCorrection("teh"));
        Assert.True(reading.Wait(BackgroundLoadTimeout));
        corrector.Dispose();
        release.Set();

        // The cancelled load releases its claim without publishing, which lets a second load start.
        Assert.True(WaitUntil(() =>
        {
            corrector.BestCorrection("teh");
            return Volatile.Read(ref loads) >= 2;
        }));
        Assert.True(corrector.IsKnownWord("teh", English));
    }

    [Fact]
    public void ConcurrentLookupsDuringLoadsStayConsistent()
    {
        using var corrector = new SymSpellCorrector(resourceLoader: language => language.Code() + "word 1000\nthe 500");
        var languages = SpellingDictionaryCatalog.AllLanguages;

        Parallel.For(0, 2000, iteration =>
        {
            var language = languages[iteration % languages.Count];
            var correction = corrector.BestCorrection(language.Code() + "wrod", language);
            Assert.True(correction is null || correction == language.Code() + "word");
        });
    }

    private static bool WaitUntil(Func<bool> condition)
    {
        return SpinWait.SpinUntil(condition, BackgroundLoadTimeout);
    }
}
