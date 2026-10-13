using Cotabby.Core.Support;

namespace Cotabby.Core.Tests;

/// <summary>
/// Each case is a space symptom reported from real use, stated as the text in the field and what the
/// model returned.
/// </summary>
public class GhostSpaceBoundaryTests
{
    private static string Adjusted(string completion, string after, bool partialWord = false) =>
        GhostSpaceBoundary.Adjusted(completion, after, continuesPartialWord: partialWord);

    /// <summary>A base model's own spacing, with the request text given.</summary>
    private static string Exact(string completion, string after, string? request = null) =>
        GhostSpaceBoundary.Adjusted(
            completion, after, continuesPartialWord: false, requestPrecedingText: request ?? after);

    [Fact]
    public void CompletionKeepsOneSpaceWhenTheFieldHasNone()
    {
        Assert.Equal(" world", Adjusted(" world", after: "Hello"));
        Assert.Equal(" world", Adjusted("world", after: "Hello"));
    }

    [Fact]
    public void CompletionDropsItsSpaceWhenTheFieldAlreadyEndsWithOne()
    {
        Assert.Equal("world", Adjusted(" world", after: "Hello "));
        Assert.Equal("world", Adjusted("world", after: "Hello "));
    }

    [Fact]
    public void RepeatedSpacesInTheCompletionCollapseToTheOneThatBelongs()
    {
        Assert.Equal(" world", Adjusted("   world", after: "Hello"));
        Assert.Equal("world", Adjusted("   world", after: "Hello "));
    }

    [Fact]
    public void NonBreakingSpaceCountsAsTheBoundaryOnBothSides()
    {
        Assert.Equal(" world", Adjusted("\u00A0world", after: "Hello"));
        Assert.Equal("world", Adjusted("world", after: "Hello\u00A0"));
    }

    [Fact]
    public void ACompletionFinishingTheUsersWordNeverTakesASpace()
    {
        Assert.Equal("iate it", Adjusted("iate it", after: "I really apprec", partialWord: true));
        Assert.Equal("llet points", Adjusted("llet points", after: "add the bu", partialWord: true));
    }

    [Fact]
    public void AnAnchoredWordTheModelEndedKeepsItsSpace()
    {
        Assert.Equal(" the timeline", Adjusted(" the timeline", after: "I was thinking that", partialWord: true));
        Assert.Equal(" on Friday", Adjusted(" on Friday", after: "at 12 PM", partialWord: true));
        Assert.Equal(" section at", Adjusted(" section at", after: "add the budget", partialWord: true));
        // A space typed meanwhile is the boundary.
        Assert.Equal("the timeline", Adjusted(" the timeline", after: "I was thinking that ", partialWord: true));
        Assert.Equal(", and", Adjusted(" , and", after: "I was thinking that", partialWord: true));
    }

    [Fact]
    public void AJoinedContinuationStaysJoined()
    {
        Assert.Equal("50K was too high", Exact("50K was too high", after: "I was thinking that 1"));
        Assert.Equal("00% accuracy", Exact("00% accuracy", after: "exactly, with 1"));
        Assert.Equal("0% consistency", Exact("0% consistency", after: "with 10"));
        Assert.Equal("s not always", Exact("s not always", after: "SPACE HANDLING. It'"));
        Assert.Equal("s", Exact("s", after: "on Friday"));
    }

    [Fact]
    public void TheModelsOwnSpaceStartsANewWord()
    {
        Assert.Equal(" on Friday", Exact(" on Friday", after: "at 12 PM"));
        Assert.Equal(" months is too long.", Exact(" months is too long.", after: "I was thinking that 12"));
        Assert.Equal(" (see page 2)", Exact(" (see page 2)", after: "the report"));
        Assert.Equal(" two spaces", Exact("  two spaces", after: "word"));
    }

    [Fact]
    public void ASpaceTypedDuringGenerationIsNotDoubled()
    {
        Assert.Equal("on Friday", Exact(" on Friday", after: "at 12 PM ", request: "at 12 PM"));
        Assert.Equal("on Friday", Exact(" on Friday", after: "at 12 PM\u00A0", request: "at 12 PM"));
    }

    [Fact]
    public void ASpaceDeletedAfterTheRequestIsRestored()
    {
        Assert.Equal(" on Friday", Exact("on Friday", after: "at 12 PM", request: "at 12 PM "));
        Assert.Equal("on Friday", Exact("on Friday", after: "at 12 PM ", request: "at 12 PM "));
    }

    [Fact]
    public void ExactSpacingKeepsTheTypographyRules()
    {
        Assert.Equal("world", Exact(" world", after: "Hello\n"));
        Assert.Equal(", regards", Exact(" , regards", after: "Best"));
        Assert.Equal("1.", Exact(" 1.", after: "Make it 1:"));
        Assert.Equal("hello", Exact(" hello", after: "she said \""));
        Assert.Equal("draft", Exact(" draft", after: "the ("));
        Assert.Equal("Hello", Exact("Hello", after: ""));
        Assert.Equal("Hello", Exact(" Hello", after: ""));
        // A completion opening with a line break takes no space.
        Assert.Equal("\nNext", Exact(" \nNext", after: "done"));
    }

    [Fact]
    public void PunctuationForAWordTheUserHasEndedIsStale()
    {
        Assert.True(GhostSpaceBoundary.IsStaleAfterTypedSpace(". I'm going", "See you on Friday "));
        Assert.True(GhostSpaceBoundary.IsStaleAfterTypedSpace(", and then", "Best "));
        Assert.False(GhostSpaceBoundary.IsStaleAfterTypedSpace(". I'm going", "See you on Friday"));
        Assert.False(GhostSpaceBoundary.IsStaleAfterTypedSpace("I'm going", "Friday. "));
        Assert.False(GhostSpaceBoundary.IsStaleAfterTypedSpace("\"hello\"", "she said "));
        Assert.False(GhostSpaceBoundary.IsStaleAfterTypedSpace("(see below)", "the report "));
    }

    [Fact]
    public void DigitsAndContractionsStayJoinedWhateverTheEngine()
    {
        Assert.Equal("50K", Adjusted("50K", after: "that 1"));
        Assert.Equal("s not always", Adjusted("s not always", after: "It'"));
        Assert.Equal("t know", Adjusted("t know", after: "I don’"));
        // A quote the word opened is closing.
        Assert.Equal(" and left", Adjusted("and left", after: "she said 'hello'"));
    }

    [Fact]
    public void PunctuationBindsToThePrecedingWord()
    {
        Assert.Equal(", regards", Adjusted(", regards", after: "Best"));
        Assert.Equal(".", Adjusted(".", after: "tonight"));
        Assert.Equal("'s report", Adjusted("'s report", after: "the team"));
    }

    [Fact]
    public void NoSpaceAfterAnOpeningBracketOrJoiner()
    {
        Assert.Equal("draft", Adjusted("draft", after: "the ("));
        Assert.Equal("mail", Adjusted("mail", after: "e-"));
    }

    [Fact]
    public void NoSpaceAfterALineBreak()
    {
        Assert.Equal("Best regards", Adjusted("Best regards", after: "See you then.\n"));
    }

    [Fact]
    public void EmptyInputsAreLeftAlone()
    {
        Assert.Equal("", Adjusted("", after: "Hello"));
        Assert.Equal("world", Adjusted("world", after: ""));
    }

    [Fact]
    public void CompletionAfterASentenceEndTakesASpace()
    {
        Assert.Equal(" The next step", Adjusted("The next step", after: "Most text is visible."));
        Assert.Equal(" and then", Adjusted("and then", after: "the draft,"));
    }

    [Fact]
    public void DigitsStayJoinedAcrossAColon()
    {
        Assert.Equal("1.", Adjusted("1.", after: "Make it 1:"));
        Assert.Equal("30 sharp", Adjusted("30 sharp", after: "at 10:"));
        Assert.Equal(" the", Adjusted("the", after: "Note:"));
    }

    [Fact]
    public void AnOpeningQuoteTakesNoSpaceAndAClosingQuoteDoes()
    {
        Assert.Equal("hello", Adjusted("hello", after: "she said \""));
        Assert.Equal(" and left", Adjusted("and left", after: "she said \"hello\""));
    }
}
