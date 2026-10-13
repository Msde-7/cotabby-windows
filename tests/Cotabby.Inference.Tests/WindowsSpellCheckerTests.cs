using Cotabby.Core.Spelling;
using Cotabby.Win32.Spelling;

namespace Cotabby.Inference.Tests;

public class WindowsSpellCheckerTests
{
    private sealed class CountingLexicon : ISpellingLexicon
    {
        public int Calls { get; private set; }

        public bool IsKnownWord(string word, SpellingDictionaryLanguage language)
        {
            Calls++;
            return true;
        }
    }

    [Fact]
    public void FlagsNonWordsAndKnowsRealOnes()
    {
        var fallback = new CountingLexicon();
        var checker = new WindowsSpellChecker(fallback);

        bool knowsHello = checker.IsKnownWord("hello", SpellingDictionaryLanguage.English);
        bool knowsTypo = checker.IsKnownWord("helllo", SpellingDictionaryLanguage.English);

        Assert.True(knowsHello);
        // A machine without the English speller answers from the fallback, which knows every word.
        Assert.Equal(fallback.Calls > 0, knowsTypo);
    }
}
