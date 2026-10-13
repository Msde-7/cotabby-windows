using Cotabby.Core.Context;

namespace Cotabby.Core.Tests.Context;

public sealed class ClipboardContentDistillerTests
{
    [Fact]
    public void ShortClipboard_IsReturnedAsIs()
    {
        const string clipboard = "line one\nline two\nline three";

        Assert.Equal(clipboard, ClipboardContentDistiller.Distill(clipboard, "completely unrelated text"));
    }

    [Fact]
    public void LongClipboard_KeepsOnlyMatchingLines()
    {
        string clipboard = string.Join('\n',
            "import Foundation", "import UIKit", "func deploy() {", "    print(\"starting deploy\")", "}");

        Assert.Equal(
            "func deploy() {\n    print(\"starting deploy\")",
            ClipboardContentDistiller.Distill(clipboard, "the deploy is running"));
    }

    [Fact]
    public void LongClipboard_WithNoMatchingLine_ReturnsItsFirst300Characters()
    {
        string clipboard = string.Join('\n', Enumerable.Range(0, 40).Select(i => $"alpha bravo charlie line {i}"));
        Assert.True(clipboard.Length > 300);

        Assert.Equal(clipboard[..300], ClipboardContentDistiller.Distill(clipboard, "completely different words"));
    }

    [Fact]
    public void Matching_IgnoresCase()
    {
        string clipboard = string.Join('\n',
            "The DEPLOYMENT pipeline", "Some unrelated header", "Another random line", "Check deployment status");

        Assert.Equal(
            "The DEPLOYMENT pipeline\nCheck deployment status",
            ClipboardContentDistiller.Distill(clipboard, "our deployment is slow"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("a b c x y z")]
    [InlineData("  \n ")]
    public void PrefixWithoutSignificantTokens_ReturnsTheClipboardAsIs(string prefix)
    {
        string clipboard = string.Join('\n', Enumerable.Range(0, 40).Select(i => $"line {i} content"));
        Assert.True(clipboard.Length > 300);

        Assert.Equal(clipboard, ClipboardContentDistiller.Distill(clipboard, prefix));
    }
}
