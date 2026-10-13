using Cotabby.Core.Support;

namespace Cotabby.Core.Tests;

public class DecodeStopPolicyTests
{
    [Theory]
    [InlineData("Hello there.", 3, 2, DecodeStopReason.SentenceBoundary)]
    [InlineData("Are you sure?", 3, 2, DecodeStopReason.SentenceBoundary)]
    [InlineData("That works!", 2, 2, DecodeStopReason.SentenceBoundary)]
    [InlineData("All done. ", 3, 2, DecodeStopReason.SentenceBoundary)]
    // Even a complete sentence does not stop before the minimum token count.
    [InlineData("Hello there.", 1, 2, null)]
    [InlineData("Hi.", 2, 3, null)]
    [InlineData("Hi.", 3, 3, DecodeStopReason.SentenceBoundary)]
    // The sentence classifier keeps these mid-thought.
    [InlineData("Please meet Dr.", 3, 2, null)]
    [InlineData("Made in the U.S.", 4, 2, null)]
    [InlineData("Pi is about 3.14", 4, 2, null)]
    [InlineData("still going strong", 5, 2, null)]
    public void SentenceBoundaryDecisions(string text, int tokens, int minimum, DecodeStopReason? expected)
    {
        Assert.Equal(expected, DecodeStopPolicy.Verdict(text, tokens, minimumTokens: minimum));
    }

    [Theory]
    [InlineData("<|im_end|>")]
    [InlineData("<|endoftext|>")]
    [InlineData("<|end|>")]
    [InlineData("<end_of_turn>")]
    [InlineData("<|eot_id|>")]
    public void EveryStopMarkerStopsEvenBelowTheMinimum(string marker)
    {
        Assert.Equal(
            DecodeStopReason.ScaffoldingMarker,
            DecodeStopPolicy.Verdict("Sounds good" + marker, tokensGenerated: 0, minimumTokens: 3));
    }

    [Fact]
    public void MarkerTakesPrecedenceOverASentenceBoundary()
    {
        Assert.Equal(DecodeStopReason.ScaffoldingMarker, DecodeStopPolicy.Verdict("Done.<|im_end|>", 5));
    }

    [Fact]
    public void MarkerMustBeCompleteInTheAccumulatedText()
    {
        // The marker can arrive split across token pieces.
        Assert.Null(DecodeStopPolicy.Verdict("Done<|im_", 3));
        Assert.Equal(DecodeStopReason.ScaffoldingMarker, DecodeStopPolicy.Verdict("Done<|im_end|>", 4));
    }

    [Fact]
    public void AngleBracketTextThatIsNotAMarkerDoesNotStop()
    {
        Assert.Null(DecodeStopPolicy.Verdict("<s>old</s> new", 5));
        Assert.Null(DecodeStopPolicy.Verdict("if a <b", 1));
    }

    [Fact]
    public void LogValuesMatchTheDecodeLogContract()
    {
        Assert.Equal("sentence_boundary", DecodeStopReason.SentenceBoundary.LogValue());
        Assert.Equal("scaffolding_marker", DecodeStopReason.ScaffoldingMarker.LogValue());
    }

    [Fact]
    public void SentenceStopWaitsForTheMinimumWordCount()
    {
        Assert.Null(DecodeStopPolicy.Verdict(" report.", 3, minimumWords: 4));
        Assert.Equal(
            DecodeStopReason.SentenceBoundary,
            DecodeStopPolicy.Verdict(" the report by Friday.", 6, minimumWords: 4));
    }
}
