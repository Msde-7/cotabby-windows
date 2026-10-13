using Cotabby.Core.Spelling;

namespace Cotabby.Core.Tests.Spelling;

public class SymSpellTests
{
    private static SymSpell MakeSymSpell()
    {
        var symSpell = new SymSpell(maxDictionaryEditDistance: 2, prefixLength: 7);
        symSpell.LoadDictionary(
            "the 1000000\nbecause 90000\nname 50000\nten 20000\ntea 15000\nreceive 8000\n"
            + "definitely 6000\nseparate 4000\noccurred 3000\ntheir 70000\nthere 80000");
        return symSpell;
    }

    private static SymSpell MakeCatSymSpell(int maxDictionaryEditDistance = 2)
    {
        var symSpell = new SymSpell(maxDictionaryEditDistance);
        symSpell.LoadDictionary("cat 10\ncart 50\ncoat 30\nact 5\n");
        return symSpell;
    }

    private static string[] Characters(string text) => text.Select(unit => unit.ToString()).ToArray();

    private static int Distance(string source, string target, int maxDistance = 2) =>
        SymSpell.DamerauOsa(Characters(source), Characters(target), maxDistance);

    [Fact]
    public void ExactMatchReturnsZeroDistance()
    {
        Assert.Equal(new SymSpellSuggestion("name", 0, 50000), MakeSymSpell().BestSuggestion("name"));
    }

    [Fact]
    public void TranspositionIsDistanceOne()
    {
        var best = MakeSymSpell().BestSuggestion("teh");
        Assert.Equal("the", best?.Term);
        Assert.Equal(1, best?.Distance);
    }

    [Theory]
    [InlineData("recieve", "receive")]
    [InlineData("becuase", "because")]
    [InlineData("definately", "definitely")]
    [InlineData("seperate", "separate")]
    [InlineData("occured", "occurred")]
    public void CorrectsCommonMisspellings(string typo, string expected)
    {
        Assert.Equal(expected, MakeSymSpell().BestSuggestion(typo)?.Term);
    }

    [Fact]
    public void LookupSortsByDistanceThenFrequency()
    {
        // "caat" is one edit from cat, cart and coat and two from act. Within a distance the more
        // frequent word leads.
        var suggestions = MakeCatSymSpell().Lookup("caat");
        Assert.Equal(["cart", "coat", "cat", "act"], suggestions.Select(s => s.Term));
        Assert.Equal([1, 1, 1, 2], suggestions.Select(s => s.Distance));
        Assert.Equal(50, suggestions[0].Count);
    }

    [Fact]
    public void LookupHonorsANarrowerEditDistanceButNeverAWiderOne()
    {
        var symSpell = MakeCatSymSpell();

        Assert.Equal(["cart", "coat", "cat"], symSpell.Lookup("caat", maxEditDistance: 1).Select(s => s.Term));
        // A request above the index's build distance is capped at it.
        Assert.Equal(["cart", "coat", "cat", "act"], symSpell.Lookup("caat", maxEditDistance: 5).Select(s => s.Term));
        Assert.Equal([new SymSpellSuggestion("cat", 0, 10)], symSpell.Lookup("cat", maxEditDistance: 0));
        Assert.Empty(symSpell.Lookup("caat", maxEditDistance: 0));
    }

    [Fact]
    public void IndexBuiltWithDistanceOneFindsNoSecondEdit()
    {
        Assert.Equal(["cart", "coat", "cat"], MakeCatSymSpell(maxDictionaryEditDistance: 1).Lookup("caat").Select(s => s.Term));
    }

    [Fact]
    public void InputFarLongerThanAnyDictionaryWordShortCircuits()
    {
        var symSpell = new SymSpell();
        symSpell.LoadDictionary("cat 10\n");
        Assert.Empty(symSpell.Lookup("catastrophe"));
    }

    [Fact]
    public void LoadDictionarySkipsMalformedLinesAndAcceptsTabs()
    {
        var symSpell = new SymSpell();
        symSpell.LoadDictionary("lonely\nword notanumber\nzero 0\nnegative -3\ntabbed\t7\n\nspaced 9 extra\n");

        Assert.Equal(2, symSpell.WordCount);
        Assert.Equal(new SymSpellSuggestion("tabbed", 0, 7), symSpell.BestSuggestion("tabbed"));
        Assert.Equal(9, symSpell.BestSuggestion("spaced")?.Count);
    }

    [Fact]
    public void LoadDictionaryToleratesCarriageReturns()
    {
        var symSpell = new SymSpell();
        symSpell.LoadDictionary("cat 10\r\ncart 50\r\n");

        Assert.Equal(2, symSpell.WordCount);
        Assert.True(symSpell.Contains("cart"));
    }

    [Fact]
    public void LoadDictionaryStopsWhenCancelled()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => new SymSpell().LoadDictionary("cat 10\n", cancellation.Token));
    }

    [Fact]
    public void DuplicateEntryKeepsTheFirstCount()
    {
        var symSpell = new SymSpell();
        symSpell.CreateDictionaryEntry("cat", 10);
        symSpell.CreateDictionaryEntry("cat", 999);

        Assert.Equal(1, symSpell.WordCount);
        Assert.Equal(10, symSpell.BestSuggestion("cat")?.Count);
    }

    [Fact]
    public void GibberishHasNoSuggestionWithinDistance()
    {
        Assert.Null(MakeSymSpell().BestSuggestion("qwxzy"));
    }

    [Fact]
    public void EmptyDictionaryReturnsNull()
    {
        var symSpell = new SymSpell();
        Assert.Equal(0, symSpell.WordCount);
        Assert.Null(symSpell.BestSuggestion("teh"));
    }

    [Fact]
    public void DamerauOsaDistances()
    {
        Assert.Equal(1, Distance("teh", "the"));
        Assert.Equal(0, Distance("cat", "cat"));
        Assert.Equal(1, Distance("kitten", "sitten"));
        // Beyond the budget returns -1.
        Assert.Equal(-1, Distance("abcdef", "uvwxyz"));
    }

    [Fact]
    public void DamerauOsa_EmptyAndLengthGapEdges()
    {
        Assert.Equal(2, Distance("", "ab"));
        Assert.Equal(-1, Distance("abc", ""));
        // A length gap larger than the budget is rejected before building the matrix.
        Assert.Equal(-1, Distance("a", "abcd"));
        // Exactly at the budget still counts.
        Assert.Equal(2, Distance("ab", "abcd"));
    }

    [Fact]
    public void EditDistanceCountsGraphemesNotUtf16Units()
    {
        // Each word has a character that takes two UTF-16 units. Dropping it is one edit, and by
        // UTF-16 units it would be two.
        var symSpell = new SymSpell(maxDictionaryEditDistance: 1);
        symSpell.LoadDictionary("ca𝒻e 10\ncafe\u0301s 5\n👨\u200D👩\u200D👧ab 3\n");

        Assert.Equal(new SymSpellSuggestion("ca𝒻e", 1, 10), symSpell.BestSuggestion("cae"));
        Assert.Equal(new SymSpellSuggestion("cafe\u0301s", 1, 5), symSpell.BestSuggestion("cafs"));
        // The family emoji is eight units and still a single deletion.
        Assert.Equal(new SymSpellSuggestion("👨\u200D👩\u200D👧ab", 1, 3), symSpell.BestSuggestion("ab"));
    }

    [Fact]
    public void LookupComposesTypedTextBeforeMatching()
    {
        var symSpell = new SymSpell();
        symSpell.LoadDictionary("café 10\n");

        // A decomposed é is canonically the same word, so it is an exact match and not a typo.
        Assert.True(symSpell.Contains("cafe\u0301"));
        Assert.Equal(new SymSpellSuggestion("café", 0, 10), symSpell.BestSuggestion("cafe\u0301"));
        Assert.Equal(new SymSpellSuggestion("café", 1, 10), symSpell.BestSuggestion("cfae\u0301"));
    }
}
