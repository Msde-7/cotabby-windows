using Cotabby.Core.Support.Output;

namespace Cotabby.Core.Tests.Support.Output;

public sealed class ScreenEchoGuardTests
{
    private const string Screen =
        "Quarterly budget review with Priya\nDo you want to save changes to C Users shore AppData Local\nSave\nDon t save\nLn 1 Col 39";

    [Fact]
    public void BorrowingANameOrShortPhraseFromTheScreenIsKept() =>
        Assert.Equal(" updated budget review with Priya.",
            ScreenEchoGuard.Trim(" updated budget review with Priya.", Screen, "I will send the"));

    [Fact]
    public void ALaterLineThatCopiesAScreenLineIsCut() =>
        Assert.Equal(" tomorrow.",
            ScreenEchoGuard.Trim(" tomorrow.\nDon't save\nCancel", Screen, "I will send it"));

    [Fact]
    public void ALongRunOfScreenWordsIsCutWhereItStarts() =>
        Assert.Equal(" soon.",
            ScreenEchoGuard.Trim(" soon. Do you want to save changes to C Users", Screen, "I will reply"));

    [Fact]
    public void TheWholeSuggestionCanBeACopy() =>
        Assert.Equal("", ScreenEchoGuard.Trim("Do you want to save changes to C Users shore", Screen, "Hello"));

    [Fact]
    public void WordsTheUserAlreadyWroteAreNotCountedAsCopied() =>
        Assert.Equal(" do you want to save changes to the draft",
            ScreenEchoGuard.Trim(" do you want to save changes to the draft", Screen,
                "I asked: do you want to save changes to the draft? And then"));

    [Fact]
    public void WithoutScreenContextNothingChanges() =>
        Assert.Equal(" anything at all", ScreenEchoGuard.Trim(" anything at all", "", "x"));

    [Fact]
    public void AStatusBarLineIsCutEvenAsTheFirstLine() =>
        Assert.Equal("", ScreenEchoGuard.Trim("Ln 1 Col 39", Screen, "I will send the`"));

    [Fact]
    public void AHeadingCopiedWholeOnTheFirstLineIsKept() =>
        Assert.Equal(" Quarterly budget review with Priya",
            ScreenEchoGuard.Trim(" Quarterly budget review with Priya", Screen, "Subject:"));

    [Fact]
    public void AStatusBarLineMatchesWhateverItsNumbersAreNow() =>
        Assert.Equal("", ScreenEchoGuard.Trim("Ln 1 Col 1\nLn\nLn", Screen, "the file to you."));
}
