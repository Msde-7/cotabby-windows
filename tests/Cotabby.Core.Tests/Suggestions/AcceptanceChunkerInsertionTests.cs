using Cotabby.Core.Suggestions;

namespace Cotabby.Core.Tests.Suggestions;

public sealed class AcceptanceChunkerInsertionTests
{
    private const string Field = "what's on your mind";

    [Theory]
    [InlineData(" you", "How are ", "you")]
    [InlineData("  you", "How are ", "you")]
    [InlineData(" you", "How are\t", "you")]
    [InlineData(" you", "How are ", "you")]
    [InlineData(" ", "Hello ", "")]
    [InlineData("\nnext", "first ", "\nnext")]
    public void InsertionChunk_DropsLeadingHorizontalWhitespaceWhenTheFieldAlreadyEndsInIt(
        string chunk, string preceding, string expected) =>
        Assert.Equal(expected, AcceptanceChunker.InsertionChunk(chunk, preceding));

    [Theory]
    [InlineData(" you", "How are")]
    [InlineData(" are", "How are you")]
    [InlineData(" you", "")]
    [InlineData("World", "")]
    [InlineData(" you", "line\n")]
    [InlineData("World", "line\n")]
    [InlineData("noon", "after")]
    [InlineData("World", "Hello")]
    [InlineData("abc", "123")]
    [InlineData("1st", "Hello")]
    [InlineData(".", "Hello")]
    [InlineData("'s", "John")]
    [InlineData(", more", "first")]
    [InlineData("World", "Hello (")]
    public void InsertionChunk_TypesTheChunkVerbatimWhenTheFieldDoesNotEndInHorizontalWhitespace(
        string chunk, string preceding) =>
        Assert.Equal(chunk, AcceptanceChunker.InsertionChunk(chunk, preceding));

    [Theory]
    [InlineData("hello", "hello ")]
    [InlineData("section 12", "section 12 ")]
    [InlineData("हिन्दी", "हिन्दी ")]
    [InlineData("done.", "done.")]
    [InlineData("really?!", "really?!")]
    [InlineData("(yes)", "(yes)")]
    [InlineData("hello ", "hello ")]
    [InlineData("資料", "資料")]
    [InlineData("", "")]
    public void AppendingTrailingSpace_AppendsOnlyAfterAFinishedSpaceDelimitedWord(string chunk, string expected) =>
        Assert.Equal(expected, AcceptanceChunker.AppendingTrailingSpace(chunk));

    [Theory]
    [InlineData("world", "world how are you", "world ")]
    [InlineData(" world", " world how", " world ")]
    [InlineData("world", "world\t  how", "world\t  ")]
    [InlineData("world", "world", "world")]
    [InlineData("line", "line\nnext", "line")]
    [InlineData("world", "world, how", "world")]
    [InlineData("done.", "done. next", "done.")]
    [InlineData("資料", "資料 です", "資料")]
    [InlineData("world", "world how", "world")]
    public void ConsumingTrailingSpace_TakesOnlyTheSpacesAndTabsAfterAFinishedWord(
        string chunk, string remaining, string expected) =>
        Assert.Equal(expected, AcceptanceChunker.ConsumingTrailingSpace(chunk, remaining));

    [Theory]
    [InlineData("hello, !!! world 123 --", 3)]
    [InlineData("", 0)]
    [InlineData("   \n\t", 0)]
    [InlineData("first\nsecond\tthird", 3)]
    [InlineData("資料 です", 2)]
    [InlineData("don't state-of-the-art", 2)]
    [InlineData("𠮷 ½", 2)]
    public void AcceptedWordCount_CountsOnlyTokensWithALetterOrNumber(string text, int expected) =>
        Assert.Equal(expected, AcceptanceChunker.AcceptedWordCount(text));

    [Theory]
    [InlineData(" today", " today")]
    [InlineData("today", " today")]
    [InlineData(" today\n", "today ")]
    public void IsStaleAcceptanceEcho_TrueForARepeatOfTheAcceptedChunkWhileTheFieldIsUnchanged(
        string result, string accepted) =>
        Assert.True(AcceptanceChunker.IsStaleAcceptanceEcho(result, accepted, Field, Field));

    [Theory]
    [InlineData(Field + " today")]
    [InlineData(Field + "x")]
    [InlineData("what's on your")]
    public void IsStaleAcceptanceEcho_FalseOnceTheFieldTextChanged(string current) =>
        Assert.False(AcceptanceChunker.IsStaleAcceptanceEcho(" today", " today", current, Field));

    [Theory]
    [InlineData(" tomorrow")]
    [InlineData(" today and tomorrow")]
    [InlineData(" Today")]
    public void IsStaleAcceptanceEcho_FalseForADifferentOrLongerContinuation(string result) =>
        Assert.False(AcceptanceChunker.IsStaleAcceptanceEcho(result, " today", Field, Field));

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\n")]
    public void IsStaleAcceptanceEcho_FalseForAnEmptyOrWhitespaceOnlyAcceptedChunk(string accepted) =>
        Assert.False(AcceptanceChunker.IsStaleAcceptanceEcho(accepted, accepted, Field, Field));
}
