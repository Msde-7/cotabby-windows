using Cotabby.Core.Spelling;

namespace Cotabby.Core.Tests.Spelling;

/// <summary>
/// A loaded dictionary keeps its delete index in flat arrays, and words added outside a load in a
/// growable table. Both must answer exactly as the one table they replaced.
/// </summary>
public class SymSpellIndexTests
{
    [Fact]
    public void ALoadedIndexAnswersExactlyLikeOneBuiltWordByWord()
    {
        var random = new Random(3);
        string Word()
        {
            var letters = new char[3 + random.Next(7)];
            for (int i = 0; i < letters.Length; i++) letters[i] = "aeioubcdlmnrst"[random.Next(14)];
            return new string(letters);
        }
        var entries = Enumerable.Range(0, 3000).Select(_ => (Word: Word(), Count: (long)random.Next(1, 50))).ToList();
        entries.Add(("ca\U0001D4BBe", 7));
        var loaded = new SymSpell();
        loaded.LoadDictionary(string.Join('\n', entries.Select(entry => $"{entry.Word} {entry.Count}")));
        var added = new SymSpell();
        foreach (var (word, count) in entries) added.CreateDictionaryEntry(word, count);

        int answered = 0;
        for (int i = 0; i < 2000; i++)
        {
            string typo = Word();
            var expected = added.Lookup(typo);
            Assert.Equal(expected, loaded.Lookup(typo));
            if (expected.Count > 1) answered++;
        }
        Assert.True(answered > 100);
        Assert.Equal(added.Lookup("cae"), loaded.Lookup("cae"));
    }

    [Fact]
    public void WordsAddedBeforeAndAfterALoadStayFindable()
    {
        var symSpell = new SymSpell();
        symSpell.CreateDictionaryEntry("qzk", 10);
        symSpell.LoadDictionary("cat 10\ncart 50\n");
        symSpell.CreateDictionaryEntry("qzkv", 20);

        Assert.Equal(new SymSpellSuggestion("qzk", 1, 10), symSpell.BestSuggestion("qzx"));
        Assert.Equal(
            [new SymSpellSuggestion("qzk", 1, 10), new SymSpellSuggestion("qzkv", 1, 20)],
            symSpell.Lookup("qzkx").OrderBy(s => s.Term));
        Assert.Equal(new SymSpellSuggestion("cart", 1, 50), symSpell.BestSuggestion("crat"));
    }

    [Fact]
    public void ACancelledLoadKeepsTheWordsAddedBeforeIt()
    {
        using var cancellation = new CancellationTokenSource();
        var symSpell = new SymSpell();
        symSpell.CreateDictionaryEntry("cart", 50);
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => symSpell.LoadDictionary("cat 10\n", cancellation.Token));
        Assert.Equal(new SymSpellSuggestion("cart", 1, 50), symSpell.BestSuggestion("crat"));
    }
}
