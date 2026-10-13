using Cotabby.Core.Suggestions;

namespace Cotabby.Core.Tests.Suggestions;

public sealed class SuggestionRequestFactoryTests
{
    private const int MaxPrefixChars = 14000;
    private const int MaxSuffixChars = 192;

    private static readonly SuggestionSettings MultiLine = new()
    {
        WordRange = new Cotabby.Core.Support.SuggestionWordRange(7, 12),
        AllowMultiLine = true,
    };

    [Fact]
    public void Build_SplitsTheTextAtTheCaret()
    {
        var field = Fields.Text("hello world", process: "chrome") with { CaretOffset = 5 };

        var request = SuggestionRequestFactory.Build(field, "request-1", SuggestionSettings.Default);

        Assert.Equal("request-1", request.RequestId);
        Assert.Equal("hello", request.Prefix);
        Assert.Equal(" world", request.Suffix);
        Assert.Equal("chrome", request.HostApp);
    }

    [Theory]
    [InlineData(-3, "", "hello")]
    [InlineData(99, "hello", "")]
    public void Build_ClampsACaretOutsideTheText(int caret, string prefix, string suffix)
    {
        var field = Fields.Text("hello") with { CaretOffset = caret };

        var request = SuggestionRequestFactory.Build(field, "request", SuggestionSettings.Default);

        Assert.Equal(prefix, request.Prefix);
        Assert.Equal(suffix, request.Suffix);
    }

    [Fact]
    public void Build_KeepsOnlyTheTextNearestTheCaret()
    {
        string before = "x" + new string('a', MaxPrefixChars);
        string after = new string('b', MaxSuffixChars) + "y";
        var field = Fields.Text(before + after) with { CaretOffset = before.Length };

        var request = SuggestionRequestFactory.Build(field, "request", SuggestionSettings.Default);

        Assert.Equal(before[1..], request.Prefix);
        Assert.Equal(after[..^1], request.Suffix);
    }

    [Theory]
    [InlineData(false, true, false, 32)]
    [InlineData(true, true, true, 16)]
    [InlineData(false, false, true, 16)]
    public void Build_IsMultiLineOnlyForAMultiLineFieldWithTheSettingOn(
        bool singleLineField, bool allowMultiLine, bool expectedSingleLine, int expectedMaxTokens)
    {
        var field = Fields.Text("hello", singleLine: singleLineField);

        var request = SuggestionRequestFactory.Build(field, "request", MultiLine with { AllowMultiLine = allowMultiLine });

        Assert.Equal(expectedSingleLine, request.SingleLine);
        Assert.Equal(expectedMaxTokens, request.MaxTokens);
    }

    [Fact]
    public void Build_CarriesTheUserNameAndLanguages()
    {
        var settings = new SuggestionSettings { UserName = "Ada", Languages = ["German"] };

        var request = SuggestionRequestFactory.Build(Fields.Text("hallo"), "request", settings);

        Assert.Equal("Ada", request.UserName);
        Assert.Contains("German", request.LanguageInstruction);
    }

    [Fact]
    public void Build_WithNoLanguages_HasAnEmptyLanguageInstruction()
    {
        var settings = new SuggestionSettings { Languages = [] };

        Assert.Equal("", SuggestionRequestFactory.Build(Fields.Text("hello"), "request", settings).LanguageInstruction);
    }
}
