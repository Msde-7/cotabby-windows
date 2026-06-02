using Cotabby.Core.Focus;
using Cotabby.Core.Suggestions;
using Xunit;

namespace Cotabby.Core.Tests;

public class SuggestionRequestFactoryTests
{
    private static FocusedField Field(string text, int caret, bool singleLine = false) => new()
    {
        ElementHandle = new object(),
        ProcessId = 0,
        ProcessName = "test",
        Text = text,
        CaretOffset = caret,
        CaretRect = ScreenRect.Empty,
        FieldRect = ScreenRect.Empty,
        IsSingleLine = singleLine,
        IsSecure = false,
    };

    [Fact]
    public void PrefixIsTextUpToCaret()
    {
        var f = Field("hello world", caret: 5);
        var r = SuggestionRequestFactory.Build(f, "req");
        Assert.Equal("hello", r.Prefix);
        Assert.Equal(" world", r.Suffix);
    }

    [Fact]
    public void CaretIsClampedIntoText()
    {
        var f = Field("abc", caret: 999);
        var r = SuggestionRequestFactory.Build(f, "req");
        Assert.Equal("abc", r.Prefix);
        Assert.Equal("", r.Suffix);
    }

    [Fact]
    public void NegativeCaret_ClampsToZero()
    {
        var f = Field("abc", caret: -3);
        var r = SuggestionRequestFactory.Build(f, "req");
        Assert.Equal("", r.Prefix);
        Assert.Equal("abc", r.Suffix);
    }

    [Fact]
    public void PrefixWindowedTo1024Chars()
    {
        var s = new string('a', 5000);
        var f = Field(s, caret: s.Length);
        var r = SuggestionRequestFactory.Build(f, "req");
        Assert.Equal(1024, r.Prefix.Length);
    }

    [Fact]
    public void SingleLineGetsShorterMaxTokens()
    {
        var single = SuggestionRequestFactory.Build(Field("hi", 2, singleLine: true), "r1");
        var multi = SuggestionRequestFactory.Build(Field("hi", 2, singleLine: false), "r2");
        Assert.True(single.MaxTokens < multi.MaxTokens);
    }

    [Fact]
    public void HostAppCarriesProcessName()
    {
        var f = Field("hi", 1) with { ProcessName = "code" };
        var r = SuggestionRequestFactory.Build(f, "req");
        Assert.Equal("code", r.HostApp);
    }
}
