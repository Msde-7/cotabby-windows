using Cotabby.Core.Suggestions;
using Cotabby.Core.Support;

namespace Cotabby.Core.Tests.Suggestions;

public sealed class SuggestionSettingsTests
{
    private static SuggestionSettings Settings(int highWords, string[] languages, bool allowMultiLine = false) => new()
    {
        WordRange = new SuggestionWordRange(1, highWords),
        Languages = languages,
        AllowMultiLine = allowMultiLine,
    };

    [Theory]
    [InlineData(7, new[] { "English" }, 10)]
    [InlineData(10, new[] { "Russian" }, 20)]
    [InlineData(12, new string[0], 16)]
    [InlineData(20, new[] { "Russian" }, 40)]
    [InlineData(20, new[] { "Russian", "German" }, 26)]
    [InlineData(50, new[] { "Russian" }, 100)]
    public void MaxTokensFor_RoundsTheWordBoundUpByTheLanguageFactor(int highWords, string[] languages, int expected) =>
        Assert.Equal(expected, Settings(highWords, languages).MaxTokensFor(singleLine: true));

    [Fact]
    public void MaxTokensFor_NeverDropsBelowTheMinimum() =>
        Assert.Equal(SuggestionSettings.MinTokens, Settings(2, ["English"]).MaxTokensFor(singleLine: true));

    [Theory]
    [InlineData(8, false, false, 16)]
    [InlineData(8, true, false, 32)]
    [InlineData(20, true, false, 80)]
    [InlineData(50, true, false, 120)]
    [InlineData(8, true, true, 16)]
    public void MaxTokensFor_DoublesUpToTheCapOnlyForMultiLineRequests(
        int highWords, bool allowMultiLine, bool singleLine, int expected) =>
        Assert.Equal(expected, Settings(highWords, ["Russian"], allowMultiLine).MaxTokensFor(singleLine));
}
