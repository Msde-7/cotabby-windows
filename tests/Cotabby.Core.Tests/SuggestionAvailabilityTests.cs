using Cotabby.Core.Focus;
using Cotabby.Core.Input;
using Cotabby.Core.Suggestions;
using Xunit;

namespace Cotabby.Core.Tests;

public class SuggestionAvailabilityTests
{
    private static FocusedField Field(string text, bool secure = false) => new()
    {
        ElementHandle = new object(),
        ProcessId = 0,
        ProcessName = "test",
        Text = text,
        CaretOffset = text.Length,
        CaretRect = ScreenRect.Empty,
        FieldRect = ScreenRect.Empty,
        IsSingleLine = false,
        IsSecure = secure,
    };

    private static KeyboardEvent K(KeyKind kind, char ch = '\0', bool down = true, bool mod = false) =>
        new() { Kind = kind, Character = ch, IsKeyDown = down, HasNonShiftModifier = mod };

    [Fact] public void NullField_NoRequest()
        => Assert.False(SuggestionAvailability.ShouldRequest(null, K(KeyKind.Character, 'a')));

    [Fact] public void SecureField_NoRequest()
        => Assert.False(SuggestionAvailability.ShouldRequest(Field("hi", secure: true), K(KeyKind.Character, 'a')));

    [Fact] public void EmptyText_NoRequest()
        => Assert.False(SuggestionAvailability.ShouldRequest(Field(""), K(KeyKind.Character, 'a')));

    [Fact] public void NonShiftModifierHeld_NoRequest()
        => Assert.False(SuggestionAvailability.ShouldRequest(Field("hi"), K(KeyKind.Character, 'a', mod: true)));

    [Fact] public void KeyUp_NoRequest()
        => Assert.False(SuggestionAvailability.ShouldRequest(Field("hi"), K(KeyKind.Character, 'a', down: false)));

    [Fact] public void NonChar_NoRequest()
        => Assert.False(SuggestionAvailability.ShouldRequest(Field("hi"), K(KeyKind.Backspace)));

    [Fact] public void CharOnPlainField_Requests()
        => Assert.True(SuggestionAvailability.ShouldRequest(Field("hi"), K(KeyKind.Character, 'a')));

    [Theory]
    [InlineData(KeyKind.Escape)]
    [InlineData(KeyKind.Enter)]
    [InlineData(KeyKind.Arrow)]
    [InlineData(KeyKind.Delete)]
    public void NavKeys_TriggerCancel(KeyKind kind)
        => Assert.True(SuggestionAvailability.ShouldCancel(K(kind)));

    [Fact] public void CharKey_DoesNotCancel()
        => Assert.False(SuggestionAvailability.ShouldCancel(K(KeyKind.Character, 'a')));
}
