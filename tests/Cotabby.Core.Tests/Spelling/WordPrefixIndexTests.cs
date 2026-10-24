using Cotabby.Core.Spelling;
using Candidate = Cotabby.Core.Spelling.WordPrefixIndex.Candidate;

namespace Cotabby.Core.Tests.Spelling;

/// <summary>The local word ending only ever appends letters to what was typed, and abstains when unsure.</summary>
public sealed class WordPrefixIndexTests
{
    [Fact]
    public void CandidatesRankByFrequencyThenAlphabeticallyAndKeepTwo()
    {
        var index = new WordPrefixIndex("schematic 8\nschema 100\nscheduled 10\nschedule 100\n");

        Assert.Equal([new Candidate("schedule", 100), new Candidate("schema", 100)], index.Candidates("sche"));
    }

    [Fact]
    public void CandidatesExcludeTheExactWordAndIgnorePrefixCase()
    {
        var index = new WordPrefixIndex("schedule 100\nscheduled 10\n");

        Assert.Equal([new Candidate("scheduled", 10)], index.Candidates("SCHEDULE"));
    }

    [Fact]
    public void IndexSkipsNonLetterAndNonPositiveEntries()
    {
        var index = new WordPrefixIndex("don't 500\nabc1 400\nabcd 0\nabcde x\nabcdef 3\n");

        Assert.Equal([new Candidate("abcdef", 3)], index.Candidates("abc"));
        Assert.Empty(index.Candidates("don"));
    }

    [Fact]
    public void ABroadPrefixAbstainsInsteadOfRankingATruncatedSubset()
    {
        string contents = string.Concat(Enumerable.Range(0, 600).Select(i => $"abc{(char)('a' + i % 26)}{(char)('a' + i / 26)} {i + 1}\n"));

        Assert.Empty(new WordPrefixIndex(contents).Candidates("abc"));
    }

    [Fact]
    public void DictionaryMarginIsInclusiveAtFourTimes()
    {
        Candidate[] atMargin = [new("because", 400), new("becalm", 100)];
        Assert.Equal("ause", WordCompletionFallback.Suffix("bec", new HashSet<string>(), atMargin));

        Candidate[] belowMargin = [new("because", 399), new("becalm", 100)];
        Assert.Null(WordCompletionFallback.Suffix("bec", new HashSet<string>(), belowMargin));
    }

    [Fact]
    public void AnAllCapsPrefixGetsAnUppercaseSuffix() =>
        Assert.Equal("SE", WordCompletionFallback.Suffix("BECAU", new HashSet<string>(), [new("because", 100)]));

    [Fact]
    public void ReferencesDifferingOnlyInCaseCountAsOneSpelling() =>
        Assert.Equal("bby", WordCompletionFallback.Suffix("cota", new HashSet<string> { "Cotabby", "cotabby" }, []));

    [Fact]
    public void ThePrefixMustBeAtLeastThreeLetters()
    {
        Candidate[] candidates = [new("because", 100)];

        Assert.Null(WordCompletionFallback.Suffix("be", new HashSet<string>(), candidates));
        Assert.Null(WordCompletionFallback.Suffix("be-c", new HashSet<string>(), candidates));
    }

    [Fact]
    public void FallbackAppendsExactLettersAndRequiresAMargin()
    {
        var index = new WordPrefixIndex("schedule 100\nscheduled 10\nschematic 8\nbeach 900\nbecause 100\n");
        var none = new HashSet<string>();

        Assert.Equal("le", WordCompletionFallback.Suffix("Schedu", none, index.Candidates("Schedu")));
        Assert.Equal("se", WordCompletionFallback.Suffix("becau", none, index.Candidates("becau")));
        var ambiguous = new WordPrefixIndex("recommend 100\nrecombine 90\n");
        Assert.Null(WordCompletionFallback.Suffix("reco", none, ambiguous.Candidates("reco")));
        Assert.Empty(index.Candidates("be"));
    }

    [Fact]
    public void ReferenceVocabularySupportsNamesButNotAmbiguousGuesses()
    {
        var words = ReferenceVocabulary.Words("Cotabby helps. Use Cota", "", "");

        Assert.DoesNotContain("Cota", words);
        Assert.Equal("bby", WordCompletionFallback.Suffix("Cota", words, []));
        Assert.Null(WordCompletionFallback.Suffix("Cota", new HashSet<string> { "Cotabby", "Cotangent" }, []));
        Assert.Null(WordCompletionFallback.Suffix("schedu", new HashSet<string>(), []));
    }

    [Fact]
    public void TheCorrectorOffersCandidatesOnceItsDictionaryLoads()
    {
        using var corrector = TestCorrector.Loaded((SpellingDictionaryLanguage.English, "schedule 100\nscheduled 10\n"));

        Assert.Equal(
            [new Candidate("schedule", 100), new Candidate("scheduled", 10)],
            corrector.CompletionCandidates("sched", SpellingDictionaryLanguage.English));
    }
}
