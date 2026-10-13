using Cotabby.Core.Spelling;
using Candidate = Cotabby.Core.Spelling.WordPrefixIndex.Candidate;

namespace Cotabby.Core.Tests.Spelling;

/// <summary>The fallback must only ever append letters to what was typed, and must abstain when ambiguous.</summary>
public class WordPrefixIndexTests
{
    private static readonly HashSet<string> NoReferences = [];

    private static string? Suffix(string prefix, params Candidate[] candidates) =>
        WordCompletionFallback.Suffix(prefix, NoReferences, candidates);

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
    public void BroadPrefixAbstains()
    {
        var words = Enumerable.Range(0, 513).Select(number => "abc" + string.Concat(number.ToString("000").Select(digit => (char)('a' + digit - '0'))));
        var lines = words.Select(word => word + " 5").ToArray();

        Assert.Equal(2, new WordPrefixIndex(string.Join('\n', lines[..512])).Candidates("abc").Count);
        Assert.Empty(new WordPrefixIndex(string.Join('\n', lines)).Candidates("abc"));
    }

    [Fact]
    public void PrefixLengthAndMatchingUseGraphemes()
    {
        var index = new WordPrefixIndex("𝒶𝒷𝒸𝒹 7\ncafés 9\ncafe\u0301ine 4\n");

        // Three letters outside the basic plane are six UTF-16 units and exactly three characters.
        Assert.Equal([new Candidate("𝒶𝒷𝒸𝒹", 7)], index.Candidates("𝒶𝒷𝒸"));
        Assert.Empty(index.Candidates("𝒶𝒷"));
        // A decomposed prefix is composed before the search, and "cafe" must not match a word whose
        // fourth character is the single grapheme e plus accent.
        Assert.Equal([new Candidate("cafés", 9)], index.Candidates("cafe\u0301"));
        Assert.Empty(index.Candidates("cafe"));
    }

    [Fact]
    public void DictionaryMarginIsInclusiveAtFourTimes()
    {
        Assert.Equal("ause", Suffix("bec", new Candidate("because", 400), new Candidate("becalm", 100)));
        Assert.Null(Suffix("bec", new Candidate("because", 399), new Candidate("becalm", 100)));
    }

    [Fact]
    public void AllCapsPrefixGetsAnUppercasedSuffix()
    {
        Assert.Equal("SE", Suffix("BECAU", new Candidate("because", 100)));
        Assert.Equal("SSE", Suffix("STRA", new Candidate("straße", 100)));
    }

    [Fact]
    public void ReferencesDifferingOnlyInCaseCountAsOneSpelling()
    {
        Assert.Equal("bby", WordCompletionFallback.Suffix("cota", new HashSet<string> { "Cotabby", "cotabby" }, []));
    }

    [Fact]
    public void PrefixMustBeAtLeastThreeLetters()
    {
        Assert.Null(Suffix("be", new Candidate("because", 100)));
        Assert.Null(Suffix("be-c", new Candidate("because", 100)));
    }

    [Fact]
    public void SuffixIsCutAtAGraphemeBoundary()
    {
        // The prefix is three characters and four UTF-16 units. The suffix starts after the whole accent.
        Assert.Equal("le", WordCompletionFallback.Suffix("e\u0301co", new HashSet<string> { "e\u0301cole" }, []));
        // "cafe" is not a prefix of a word whose fourth character is e plus accent.
        Assert.Null(WordCompletionFallback.Suffix("cafe", new HashSet<string> { "cafe\u0301s" }, []));
    }

    [Fact]
    public void FallbackAppendsExactLettersAndRequiresAMargin()
    {
        var index = new WordPrefixIndex("schedule 100\nscheduled 10\nschematic 8\nbeach 900\nbecause 100\n");
        Assert.Equal("le", WordCompletionFallback.Suffix("Schedu", NoReferences, index.Candidates("Schedu")));
        Assert.Equal("se", WordCompletionFallback.Suffix("becau", NoReferences, index.Candidates("becau")));

        var ambiguous = new WordPrefixIndex("recommend 100\nrecombine 90\n");
        Assert.Null(WordCompletionFallback.Suffix("reco", NoReferences, ambiguous.Candidates("reco")));
        Assert.Empty(index.Candidates("be"));
    }

    [Fact]
    public void ReferenceVocabularySupportsNamesButNotAmbiguousGuesses()
    {
        var words = WordCompletionFallback.ReferenceWords("Cotabby helps. Use Cota", trailingText: "", glossary: "");
        Assert.DoesNotContain("Cota", words);
        Assert.Equal("bby", WordCompletionFallback.Suffix("Cota", words, []));
        Assert.Null(WordCompletionFallback.Suffix("Cota", new HashSet<string> { "Cotabby", "Cotangent" }, []));
        Assert.Null(Suffix("schedu"));
    }

    [Fact]
    public void ReferenceWordsComeFromAllThreeSourcesAndKeepConnectors()
    {
        var words = WordCompletionFallback.ReferenceWords("It's a well-known -fact- (re\u0301sume\u0301) x2 par", "tail text", "Glossary");
        Assert.Equal(
            new HashSet<string> { "It's", "a", "well-known", "re\u0301sume\u0301", "x", "tail", "text", "Glossary" },
            words);
    }
}
