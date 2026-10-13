using Cotabby.Core.Spelling;
using static Cotabby.Core.Spelling.SpellingDictionaryLanguage;

namespace Cotabby.Core.Tests.Spelling;

/// <summary>Dictionaries come from LoadDictionary or an injected loader, so no bundled word list is read.</summary>
public class SymSpellCorrectorTests
{
    private static readonly TimeSpan BackgroundLoadTimeout = TimeSpan.FromSeconds(60);

    private static SymSpellCorrector MakeLoadedCorrector()
    {
        var corrector = new SymSpellCorrector(resourceLoader: _ => null);
        corrector.LoadDictionary("the 1000000\nname 50000\nreceive 8000\nseparate 4000");
        return corrector;
    }

    [Fact]
    public void ReturnsNullBeforeIndexIsReady()
    {
        using var corrector = new SymSpellCorrector(resourceLoader: _ => null);
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
        using var corrector = new SymSpellCorrector(resourceLoader: _ => null);
        corrector.LoadDictionary("gift 1000\ngive 500", English);
        corrector.LoadDictionary("gibt 1000\ngift 10", German);

        Assert.Equal("gift", corrector.BestCorrection("gibt", English));
        Assert.Null(corrector.BestCorrection("gibt", German));
    }

    [Fact]
    public void EvictsLeastRecentlyUsedLanguageAtCacheLimit()
    {
        using var corrector = new SymSpellCorrector(cacheLimit: 2, resourceLoader: _ => null);
        corrector.LoadDictionary("the 1000", English);
        corrector.LoadDictionary("das 1000", German);

        // Touch English after German so German becomes the least recently used entry.
        Assert.Null(corrector.BestCorrection("the", English));
        corrector.LoadDictionary("hola 1000", Spanish);

        Assert.Equal([English, Spanish], corrector.CachedLanguages);
    }

    [Fact]
    public void CacheLimitIsClampedToAtLeastOneLanguage()
    {
        using var corrector = new SymSpellCorrector(cacheLimit: 0, resourceLoader: _ => null);
        corrector.LoadDictionary("the 1000", English);
        corrector.LoadDictionary("das 1000", German);

        Assert.Equal([German], corrector.CachedLanguages);
    }

    [Fact]
    public void CompletionCandidatesAreEmptyWhileColdAndRankedOnceLoaded()
    {
        using var corrector = new SymSpellCorrector(resourceLoader: _ => null);
        Assert.Empty(corrector.CompletionCandidates("rec", English));

        corrector.LoadDictionary("receive 8000\nrecent 9000\nrecipe 100\nseparate 4000");

        // Highest frequency first, capped at two candidates.
        Assert.Equal(["recent", "receive"], corrector.CompletionCandidates("rec", English).Select(c => c.Word));
    }

    [Fact]
    public void KnownWordsFailOpenWhileColdAndIgnoreCaseOnceLoaded()
    {
        using var corrector = new SymSpellCorrector(resourceLoader: _ => null);
        Assert.True(corrector.IsKnownWord("nmae", English));

        corrector.LoadDictionary("name 50000");

        Assert.False(corrector.IsKnownWord("nmae", English));
        Assert.True(corrector.IsKnownWord("Name", English));
        // German is still cold, so nothing can vouch that the word is wrong there.
        Assert.True(corrector.IsKnownWord("nmae", German));
    }

    [Fact]
    public void PreloadLanguageLoadsAtConstruction()
    {
        using var requested = new ManualResetEventSlim();
        SpellingDictionaryLanguage? requestedLanguage = null;
        using var corrector = new SymSpellCorrector(preloadLanguage: German, resourceLoader: language =>
        {
            requestedLanguage = language;
            requested.Set();
            return null;
        });

        Assert.True(requested.Wait(BackgroundLoadTimeout));
        Assert.Equal(German, requestedLanguage);
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

        Assert.True(WaitUntil(() => corrector.CachedLanguages.SequenceEqual([French])), "load never published");
        Assert.Equal(1, calls);
        Assert.Equal("bonjour", corrector.BestCorrection("bonjoor", French));
    }

    [Fact]
    public void MissingDictionaryResourceFailsOpenAndCachesNothing()
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

        // A failed load publishes nothing, so lookups keep failing open and the cache stays empty.
        Assert.Null(corrector.BestCorrection("ciaoo", Italian));
        Assert.Empty(corrector.CachedLanguages);
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
        Assert.Empty(corrector.CachedLanguages);
    }

    [Fact]
    public void ConcurrentLookupsDuringLoadsStayConsistent()
    {
        using var corrector = new SymSpellCorrector(cacheLimit: 2, resourceLoader: language => language.Code() + "word 1000\nthe 500");
        var languages = SpellingDictionaryCatalog.AllLanguages;

        Parallel.For(0, 2000, iteration =>
        {
            var language = languages[iteration % languages.Count];
            var correction = corrector.BestCorrection(language.Code() + "wrod", language);
            Assert.True(correction is null || correction == language.Code() + "word");
            Assert.InRange(corrector.CachedLanguages.Count, 0, 2);
        });
    }

    private static bool WaitUntil(Func<bool> condition)
    {
        return SpinWait.SpinUntil(condition, BackgroundLoadTimeout);
    }
}
