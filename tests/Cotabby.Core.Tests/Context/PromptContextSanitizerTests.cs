using Cotabby.Core.Context;

namespace Cotabby.Core.Tests.Context;

public sealed class PromptContextSanitizerTests
{
    [Theory]
    [InlineData("raw-output", "raw output")]
    [InlineData("hello    world", "hello world")]
    [InlineData("a\t\tb", "a b")]
    [InlineData("first\n   \n\nsecond", "first\nsecond")]
    [InlineData("one\r\ntwo", "one\ntwo")]
    [InlineData("Hello world 123 user@host.com", "Hello world 123 user@host.com")]
    [InlineData("\u001B[31mERROR\u001B[0m something broke", "ERROR something broke")]
    [InlineData("\u001B[32mHello\u001B[0m world", "Hello world")]
    [InlineData("   \n  \n  ", "")]
    [InlineData("", "")]
    public void Sanitize_NormalizesToPromptSafeText(string input, string expected)
    {
        Assert.Equal(expected, PromptContextSanitizer.Sanitize(input));
    }

    [Fact]
    public void SignificantTokens_SplitsLowercasesAndFiltersByLength()
    {
        Assert.Equal(
            ["hello", "swift", "world"],
            PromptContextSanitizer.SignificantTokens("Hello, hello WORLD! swift-ui an").Order());
        Assert.Equal(
            ["an", "far", "ox", "ran"],
            PromptContextSanitizer.SignificantTokens("an ox ran far", minimumLength: 2).Order());
        Assert.Empty(PromptContextSanitizer.SignificantTokens("--- ,,,"));
    }

    [Theory]
    [InlineData("---a---", true)]
    [InlineData("--- 7", true)]
    [InlineData("東", true)]
    [InlineData("--- ---", false)]
    [InlineData("", false)]
    public void ContainsAlphanumericSignal(string text, bool expected)
    {
        Assert.Equal(expected, PromptContextSanitizer.ContainsAlphanumericSignal(text));
    }
}
