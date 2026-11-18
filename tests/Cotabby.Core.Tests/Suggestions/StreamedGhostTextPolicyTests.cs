using Cotabby.Core.Suggestions;

namespace Cotabby.Core.Tests.Suggestions;

/// <summary>Ported from upstream StreamedGhostTextPolicyTests.</summary>
public sealed class StreamedGhostTextPolicyTests
{
    private static string Buffered(string text) =>
        StreamedGhostTextPolicy.CompletedBufferedPrediction(text, visibleCharacterCount: "world".Length);

    [Theory]
    [InlineData("world ag", "world ")]
    [InlineData("world again tom", "world again ")]
    [InlineData("world again\n  tom", "world again\n  ")]
    [InlineData("world ag\t", "world ag\t")]
    public void HiddenTrailingFragmentWaitsForItsBoundary(string text, string expected) =>
        Assert.Equal(expected, Buffered(text));

    [Theory]
    [InlineData("world again.")]
    [InlineData("world again,")]
    [InlineData("world again!")]
    [InlineData("world (again)")]
    [InlineData("world \"again\"")]
    [InlineData("world 'again'")]
    [InlineData("world ‘again’")]
    public void PunctuationCompletesHiddenWords(string text) => Assert.Equal(text, Buffered(text));

    [Theory]
    [InlineData("don't")]
    [InlineData("don'")]
    [InlineData("state-of-the-art")]
    [InlineData("state-")]
    [InlineData("item_2")]
    [InlineData("42")]
    [InlineData("(ag")]
    public void ConnectorsAndIdentifiersDoNotCompleteHiddenTokens(string token) =>
        Assert.Equal("world ", Buffered("world " + token));

    [Theory]
    [InlineData("world ag", 7, "world a")]
    [InlineData("world ag", 8, "world ag")]
    [InlineData("world ag", 99, "world ag")]
    [InlineData("world ag", -1, "world ")]
    [InlineData("  ag", 0, "  ")]
    [InlineData("", 0, "")]
    public void VisibleCharactersSurviveTrimming(string text, int visible, string expected) =>
        Assert.Equal(expected, StreamedGhostTextPolicy.CompletedBufferedPrediction(text, visible));

    [Fact]
    public void BoundariesCountUserCharactersRatherThanUtf16Units()
    {
        const string visible = "\U0001F408 café";
        Assert.Equal(visible + " ", StreamedGhostTextPolicy.CompletedBufferedPrediction(visible + " cafe\u0301", 6));
        Assert.Equal("你好", StreamedGhostTextPolicy.CompletedBufferedPrediction("你好世界", 2));
    }

    [Theory]
    [InlineData(" wor", null, true)]
    [InlineData(" wor", "", true)]
    [InlineData("", null, false)]
    [InlineData("", " wor", false)]
    [InlineData(" world", " wor", true)]
    [InlineData(" wor", " world", false)]
    [InlineData(" world", " world", false)]
    [InlineData(" worse idea", " world", false)]
    [InlineData(" cafe\u0301", " cafe", false)]
    public void RenderableExtensionRequiresAStrictlyLongerPrefixExtension(string candidate, string? rendered, bool expected) =>
        Assert.Equal(expected, StreamedGhostTextPolicy.IsRenderableExtension(candidate, rendered));
}
