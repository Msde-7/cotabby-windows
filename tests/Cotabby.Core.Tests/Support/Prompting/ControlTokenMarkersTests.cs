using Cotabby.Core.Support;

namespace Cotabby.Core.Tests;

public class ControlTokenMarkersTests
{
    [Theory]
    [InlineData("a<|im_start|>b", "ab")]
    [InlineData("<start_of_turn>hello", "hello")]
    [InlineData("<|user|>x<|assistant|>y<|system|>z", "xyz")]
    [InlineData("[INST]do it[/INST]the answer", "do itthe answer")]
    // An unpaired Llama-3 header marker falls back to per-marker removal.
    [InlineData("text<|end_header_id|> more", "text more")]
    public void OpeningMarkersAreRemovedInPlace(string input, string expected)
    {
        Assert.Equal(expected, ControlTokenMarkers.Sanitize(input));
    }

    [Theory]
    [InlineData("good answer<|im_end|>garbage", "good answer")]
    [InlineData("keep<|endoftext|>tail", "keep")]
    [InlineData("keep<|end|>tail", "keep")]
    [InlineData("reply text<end_of_turn>more", "reply text")]
    [InlineData("Hello there<|eot_id|>x", "Hello there")]
    // The earliest stop marker wins regardless of list order.
    [InlineData("keep<|endoftext|>drop<|im_end|>also drop", "keep")]
    public void StopMarkersTruncateTheRest(string input, string expected)
    {
        Assert.Equal(expected, ControlTokenMarkers.Sanitize(input));
    }

    [Theory]
    [InlineData("Sure thing.<|im_end|><|im_start|>user\nWhat else?", "Sure thing.")]
    [InlineData("<|im_start|><|im_end|>", "")]
    public void SalvagesPrefixThenDropsHallucinatedTurn(string input, string expected)
    {
        Assert.Equal(expected, ControlTokenMarkers.Sanitize(input));
    }

    [Theory]
    [InlineData("<|start_header_id|>assistant<|end_header_id|>Hello there", "Hello there")]
    [InlineData("<|start_header_id|>user<|end_header_id|>What time is it?", "What time is it?")]
    // The pattern is lazy, so text between two blocks survives.
    [InlineData(
        "<|start_header_id|>assistant<|end_header_id|>Hi <|start_header_id|>user<|end_header_id|>there", "Hi there")]
    public void Llama3HeaderBlockDropsRoleName(string input, string expected)
    {
        Assert.Equal(expected, ControlTokenMarkers.Sanitize(input));
    }

    [Theory]
    [InlineData("a normal sentence, continuing on")]
    [InlineData("strike <s>this</s> out")]
    public void LeavesOrdinaryTextAndHtmlStrikethroughUnchanged(string input)
    {
        Assert.Equal(input, ControlTokenMarkers.Sanitize(input));
    }

    [Theory]
    [InlineData(
        " to unplug the router for 10 seconds.\n<|im_start|>user\nHow do I reset my router?<|im_end|>",
        " to unplug the router for 10 seconds.\n")]
    [InlineData(" challenging hike with stunning views.\n  <|user|>\nWhat else?", " challenging hike with stunning views.\n  ")]
    // A leading marker is removed in place.
    [InlineData("<|im_start|>assistant\nHello", "assistant\nHello")]
    // With no text before the line, nothing ended.
    [InlineData("\n<|im_start|>Hello", "\nHello")]
    public void ATurnOpeningOnItsOwnLineEndsTheContinuation(string input, string expected)
    {
        Assert.Equal(expected, ControlTokenMarkers.Sanitize(input));
    }
}
