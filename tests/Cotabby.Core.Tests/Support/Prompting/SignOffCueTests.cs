using Cotabby.Core.Support;

namespace Cotabby.Core.Tests;

/// <summary>The cue must fire exactly at a closing line and nowhere else.</summary>
public class SignOffCueTests
{
    [Theory]
    [InlineData("see you Friday.\nBest,")]
    [InlineData("Thanks again,")]
    [InlineData("Kind regards")]
    public void ClosingLineWithTheCaretAfterItsComma(string prefix)
    {
        Assert.True(SignOffCue.PrecedesSignature(prefix));
    }

    [Theory]
    [InlineData("see you Friday.\n\nBest,\n")]
    [InlineData("Cheers!\n   ")]
    [InlineData("see you Friday.\r\n\r\nBest,\r\n")]
    public void ClosingLineWithTheCaretOnTheNextLine(string prefix)
    {
        Assert.True(SignOffCue.PrecedesSignature(prefix));
    }

    [Theory]
    [InlineData("— Thanks again!")]
    [InlineData("BEST  REGARDS:")]
    [InlineData("with gratitude,")]
    public void PunctuationCaseAndSpacingDoNotMatter(string prefix)
    {
        Assert.True(SignOffCue.PrecedesSignature(prefix));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Hi")]
    [InlineData("Hi Sarah,")]
    [InlineData("Thanks for")]
    [InlineData("I wish you the best,")]
    [InlineData("thanks for sending over the draft.")]
    public void OpeningsAndOrdinaryProseNeverQualify(string prefix)
    {
        Assert.False(SignOffCue.PrecedesSignature(prefix));
    }

    [Theory]
    [InlineData("See you Friday. Thanks,", true)]
    [InlineData("Could you add the numbers before Friday? Thanks again,", true)]
    [InlineData("Let me know what you think! Best,", true)]
    [InlineData("Thanks for the update.", false)]
    [InlineData("See you Friday. I wish you the best,", false)]
    [InlineData("It was great. Thanks to everyone who", false)]
    public void AClosingFoldedOntoTheLastSentenceStillQualifies(string prefix, bool expected)
    {
        Assert.Equal(expected, SignOffCue.PrecedesSignature(prefix));
    }

    [Theory]
    [InlineData("Best,\nSam")]
    [InlineData("Thanks, Sam")]
    public void AnAlreadySignedClosingDoesNotQualify(string prefix)
    {
        Assert.False(SignOffCue.PrecedesSignature(prefix));
    }
}
