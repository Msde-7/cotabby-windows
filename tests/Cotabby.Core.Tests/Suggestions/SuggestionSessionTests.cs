using Cotabby.Core.Suggestions;

namespace Cotabby.Core.Tests.Suggestions;

public sealed class SuggestionSessionTests
{
    private static SuggestionSession Session(string predicted, int? firstOffer = null, bool following = true) => new()
    {
        RequestId = "request",
        PredictedText = predicted,
        FirstOfferLength = firstOffer,
        ShowFollowingWords = following,
    };

    [Fact]
    public void AFirstOfferPastTheTextIsNoLimit() =>
        Assert.Equal("abc", Session("abc", firstOffer: 99).VisibleText);

    [Fact]
    public void FollowingWordsWaitBehindAWordEnding()
    {
        var session = Session("beam for the device", firstOffer: 4);
        Assert.Equal("beam", session.VisibleText);

        var partlyTyped = session.Advancing(2);
        Assert.Equal("am", partlyTyped.VisibleText);
        Assert.Equal("am for the device", partlyTyped.PredictedText);

        var completedWord = partlyTyped.Advancing(2);
        Assert.Equal(" for the device", completedWord.VisibleText);
        Assert.False(completedWord.IsExhausted);
    }

    [Fact]
    public void OneWordAtATimeRollsThroughTheSamePrediction()
    {
        var session = Session(" hello world again", following: false);

        foreach (string offer in new[] { " hello", " world", " again" })
        {
            Assert.Equal(offer, session.VisibleText);
            Assert.False(session.IsExhausted);
            session = session.Advancing(offer.Length);
        }
        Assert.True(session.IsExhausted);
        Assert.Equal("", session.VisibleText);
    }

    [Fact]
    public void OneWordAtATimeLeavesTrailingPunctuationWhenTheUserAcceptsItSeparately()
    {
        var session = Session(" hello, world", following: false) with { AcceptPunctuationWithWord = false };

        Assert.Equal(" hello", session.VisibleText);
        Assert.Equal(",", session.Advancing(6).VisibleText);
    }

    [Fact]
    public void ACorrectionAlwaysShowsWhole()
    {
        var session = Session("the one", following: false) with { Correction = new TypoCorrection("teh", "the one") };

        Assert.Equal("the one", session.VisibleText);
    }

    [Fact]
    public void TypingCrossesTheFirstOfferWithoutDroppingTheFollowingWords()
    {
        var session = Session("beam for the device", firstOffer: 4);

        foreach (char c in "beam for")
        {
            session = SuggestionSessionReconciler.Apply(session, Keys.Char(c)).Next!;
        }

        Assert.Equal(" the device", session.VisibleText);
    }

    [Fact]
    public void TypingThroughAWordRevealsOnlyTheNextOneWhenFollowingWordsAreOff()
    {
        var session = Session(" hello world again", following: false);

        foreach (char c in " hello ")
        {
            session = SuggestionSessionReconciler.Apply(session, Keys.Char(c)).Next!;
        }

        Assert.Equal("world", session.VisibleText);
        Assert.Equal("world again", session.PredictedText);
    }
}
