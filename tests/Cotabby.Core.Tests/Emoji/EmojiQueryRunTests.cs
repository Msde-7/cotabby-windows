using Cotabby.Core.Emoji;
using Cotabby.Core.Focus;

namespace Cotabby.Core.Tests.Emoji;

public class EmojiQueryRunTests
{
    private static FocusedField Field(string text, int caretOffset) => new()
    {
        ElementHandle = new object(),
        ProcessId = 1,
        ProcessName = "test",
        Text = text,
        CaretOffset = caretOffset,
        CaretRect = ScreenRect.Empty,
        FieldRect = ScreenRect.Empty,
        IsSingleLine = false,
        IsSecure = false,
    };

    [Theory]
    [InlineData("hello :smile", 6)]
    [InlineData("hello :smile:", 7)]
    [InlineData("nice :+1", 3)]
    [InlineData("a :", 1)]
    [InlineData(":tada done :smile", 6)]
    // "::" is the closing colon commit of an empty query and the run is both colons.
    [InlineData("a ::", 2)]
    // Astral characters earlier in the field cannot skew the count.
    [InlineData("hi 👋🏽 :wave", 5)]
    public void MeasuresTrailingRun(string precedingText, int expected) =>
        Assert.Equal(expected, EmojiQueryRun.TrailingRunLength(precedingText));

    [Theory]
    [InlineData("hello world")]
    [InlineData("")]
    [InlineData(":smile not anymore")]
    [InlineData(":smi.le")]
    [InlineData(":smile\n")]
    public void ReturnsNullWhenNoTrailingRun(string precedingText) =>
        Assert.Null(EmojiQueryRun.TrailingRunLength(precedingText));

    [Fact]
    public void DeleteCount_ComesFromTheFieldsMeasuredRunNotTheTypedLength()
    {
        // Host autocorrect or a dropped keystroke left only ":smi" although ":smile" was typed.
        // Deleting the typed length would eat two unrelated characters.
        Assert.Equal(4, EmojiQueryRun.DeleteCount(Field("hi :smi", 7), fallback: 6));
    }

    [Fact]
    public void DeleteCount_FallsBackToTypedLengthWhenFieldHasNoQueryRun() =>
        Assert.Equal(6, EmojiQueryRun.DeleteCount(Field("done.", 5), fallback: 6));

    [Fact]
    public void DeleteCount_OnlyMeasuresTextBeforeTheCaret()
    {
        Assert.Equal(6, EmojiQueryRun.DeleteCount(Field("hi :smile and more", 9), fallback: 1));
        Assert.Equal(6, EmojiQueryRun.DeleteCount(Field(":smile", 99), fallback: 1));
    }
}
