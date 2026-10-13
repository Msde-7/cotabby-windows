namespace Cotabby.Core.Spelling;

/// <summary>
/// The word knowledge behind typo detection. It stands in for the system spell checker that the
/// macOS app queries. <see cref="SymSpellCorrector"/> answers from the bundled dictionaries.
/// </summary>
public interface ISpellingLexicon
{
    /// <summary>
    /// False only when the lexicon can vouch that <paramref name="word"/> is not a word of
    /// <paramref name="language"/>. A lexicon that is not ready answers true, so detection fails open.
    /// </summary>
    bool IsKnownWord(string word, SpellingDictionaryLanguage language);
}

/// <summary>
/// Typo detection for the typo gate. Detection only flags non-words, so a real word used in the
/// wrong place, such as <c>their</c> for <c>there</c>, is never detected.
/// </summary>
public sealed class CurrentWordSpellChecker(ISpellingLexicon lexicon)
{
    /// <summary>
    /// True when the entire token is one misspelled word. A token that carries punctuation, an
    /// apostrophe or a hyphen is several words to a spell checker and is never flagged as a whole,
    /// which keeps <c>nmae,</c>, <c>I'm</c> and <c>well-knwon</c> from misfiring.
    /// </summary>
    public bool IsTypo(string word, SpellingDictionaryLanguage language) =>
        word.Length > 0
        && Graphemes.Split(word).All(Graphemes.IsLetter)
        && !lexicon.IsKnownWord(word, language);
}
