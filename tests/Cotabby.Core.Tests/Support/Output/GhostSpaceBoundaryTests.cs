using Cotabby.Core.Support;

namespace Cotabby.Core.Tests;

/// <summary>
/// Each case is a space symptom reported from real use, stated as the text in the field and what the
/// model returned.
/// </summary>
public class GhostSpaceBoundaryTests
{
    /// <summary>A base model's own spacing, with the request text given.</summary>
    private static string Exact(string completion, string after, string? request = null) =>
        GhostSpaceBoundary.LiveAdjusted(completion, after, request ?? after);

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
        Assert.Equal("mail", Exact(" mail", after: "e-"));
        // A straight quote that closes a pair takes a space after it.
        Assert.Equal(" and left", Exact(" and left", after: "she said \"hello\""));
        Assert.Equal(" the", Exact(" the", after: "Note:"));
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
    public void AnEngineThatDoesNotVouchForItsSpacingOnlyLosesASpaceTheFieldSupplies()
    {
        Assert.Equal("world", GhostSpaceBoundary.LiveAdjusted(" world", "Hello ", requestPrecedingText: null));
        Assert.Equal(" world", GhostSpaceBoundary.LiveAdjusted(" world", "Hello", requestPrecedingText: null));
        Assert.Equal("world", GhostSpaceBoundary.LiveAdjusted("world", "Hello", requestPrecedingText: null));
    }
}
