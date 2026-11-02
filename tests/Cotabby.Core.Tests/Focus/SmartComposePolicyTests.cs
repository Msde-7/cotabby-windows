using Cotabby.Core.Focus;

namespace Cotabby.Core.Tests.Focus;

public sealed class SmartComposePolicyTests
{
    private static FocusedField Gmail(string text, int caret, string process = "chrome", string title = "Inbox - me@gmail.com - Gmail") => new()
    {
        ProcessId = 1,
        ProcessName = process,
        Text = text,
        CaretOffset = caret,
        CaretRect = new ScreenRect(0, 0, 1, 16),
        FieldRect = new ScreenRect(0, 0, 400, 200),
        IsSingleLine = false,
        IsSecure = false,
        WindowTitle = title,
    };

    [Theory]
    [InlineData("lot of time\ntab", 15)]
    [InlineData("the app\ntab\n\n--\nSam", 11)]
    [InlineData("the app\r\ntab\r\n--\r\nSam", 12)]
    public void SuggestionLength_RunsThroughTheHint(string trailing, int expected) =>
        Assert.Equal(expected, SmartComposePolicy.SuggestionLength(trailing));

    [Theory]
    [InlineData("def\ntabular")]
    [InlineData("def")]
    [InlineData("\ntab")]
    [InlineData("")]
    public void SuggestionLength_IsZeroForOtherShapes(string trailing) =>
        Assert.Equal(0, SmartComposePolicy.SuggestionLength(trailing));

    [Fact]
    public void SuggestionLength_IgnoresAParagraph() =>
        Assert.Equal(0, SmartComposePolicy.SuggestionLength(new string('a', 161) + "\ntab"));

    [Fact]
    public void Apply_CutsGmailsSuggestionAndKeepsTheSignature()
    {
        var field = SmartComposePolicy.Apply(Gmail("It took a the app\ntab\n\n--\nSam", 10));

        Assert.True(field.HostIsPredicting);
        Assert.Equal("It took a \n\n--\nSam", field.Text);
        Assert.Equal("It took a ", field.PrecedingText);
    }

    [Theory]
    [InlineData("chrome", "Example Domain")]
    [InlineData("notepad", "Gmail notes - Notepad")]
    public void Apply_LeavesOtherPagesAndAppsAlone(string process, string title)
    {
        var field = Gmail("abcdef\ntab", 3, process, title);

        Assert.Same(field, SmartComposePolicy.Apply(field));
    }

    [Fact]
    public void Apply_LeavesASelectionAlone()
    {
        var field = Gmail("abcdef\ntab", 3) with { HasSelection = true };

        Assert.Same(field, SmartComposePolicy.Apply(field));
    }
}
