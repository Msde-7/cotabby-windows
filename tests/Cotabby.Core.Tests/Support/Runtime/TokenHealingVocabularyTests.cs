using System.Text;
using Cotabby.Core.Support;

namespace Cotabby.Core.Tests;

public class TokenHealingVocabularyTests
{
    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    private static TokenHealingVocabulary Vocabulary(params (int Token, string Piece)[] entries) =>
        new(entries.Select(entry => (entry.Token, Utf8(entry.Piece))));

    private static int[] Matching(TokenHealingVocabulary vocabulary, byte[] prefix, bool singleLine = false) =>
        vocabulary.MatchingTokens(prefix, singleLine).Order().ToArray();

    private static readonly TokenHealingVocabulary Words = new(
    [
        (1, Utf8("sched")), (2, Utf8("schedule")), (3, Utf8("scheduling")), (4, Utf8("sch")),
        (5, Utf8("send")), (6, Utf8(" schedule")), (7, Utf8(" ")), (8, Utf8("")), (9, Utf8("sched")),
        (10, Utf8("\n")), (11, Utf8("\n\n")), (12, [0xC3]), (13, Utf8("éclair")),
    ]);

    [Fact]
    public void CoveringTokensIncludeExactReplayAndLongerWords()
    {
        // An unfinished word can choose a whole-word token. A completed word can replay itself.
        Assert.Equal([1, 2, 3, 9], Matching(Words, Utf8("sched")));
        Assert.Equal([2], Matching(Words, Utf8("schedule")));
        Assert.Equal([6, 7], Matching(Words, Utf8(" ")));
        Assert.Equal([10, 11], Matching(Words, Utf8("\n")));
        Assert.Equal([13], Matching(Words, Utf8("é")));
    }

    [Fact]
    public void ShorterTokensDoNotCompeteWithCoveringTokens()
    {
        // Neither " i" nor " in" accounts for the known final "t" of " int".
        var technical = Vocabulary((20, " i"), (21, " in"), (22, " int"), (23, " intelligence"), (24, " internal"));
        Assert.Equal([22, 23, 24], Matching(technical, Utf8(" int")));
    }

    [Fact]
    public void SpellingWithNoCoveringTokenProgressesThroughShorterPieces()
    {
        Assert.Equal([1, 4, 9], Matching(Words, Utf8("schedx")));
        Assert.Equal([12], Matching(Words, Utf8("éx")));
    }

    [Fact]
    public void SingleLineFiltersLineBreakTokensBeforeChoosingABranch()
    {
        var lineBreaks = Vocabulary((30, "foo\n"), (31, "f"), (32, "oo"), (33, "foo\r\n"));
        Assert.Equal([30, 33], Matching(lineBreaks, Utf8("foo")));
        // Single-line sampling forbids the only covering tokens, so shorter pieces must remain.
        Assert.Equal([31], Matching(lineBreaks, Utf8("foo"), singleLine: true));
        Assert.Equal([32], Matching(lineBreaks, Utf8("oo"), singleLine: true));
    }

    [Fact]
    public void UnknownAndEmptyPrefixesMatchNothing()
    {
        Assert.Empty(Matching(Words, Utf8("xyz")));
        Assert.Empty(Matching(Words, []));
    }
}
