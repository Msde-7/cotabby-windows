using Cotabby.Core.Focus;
using Cotabby.Core.Overlay;
using Cotabby.Core.Tests.Suggestions;

namespace Cotabby.Core.Tests.Focus;

public sealed class OverlayStylePolicyTests
{
    private static FocusedField Field(string text, int caret, bool estimated = false) =>
        Fields.Text(text) with { CaretOffset = caret, CaretIsEstimated = estimated };

    [Fact]
    public void Correction_AlwaysUsesItsOwnCard() =>
        Assert.Equal(
            OverlayStyle.Correction,
            OverlayStylePolicy.StyleFor(SuggestionDisplayMode.Inline, Field("teh ", 4), isCorrection: true));

    [Theory]
    [InlineData("hello", 5, OverlayStyle.Inline)]
    [InlineData("hello   ", 5, OverlayStyle.Inline)]
    [InlineData("hello\nworld", 5, OverlayStyle.Inline)]
    [InlineData("hello world", 5, OverlayStyle.Popup)]
    public void Auto_UsesACardOnlyWhenTextFollowsTheCaretOnItsLine(string text, int caret, OverlayStyle expected) =>
        Assert.Equal(expected, OverlayStylePolicy.StyleFor(SuggestionDisplayMode.Auto, Field(text, caret), isCorrection: false));

    [Fact]
    public void Auto_UsesACardWhenTheCaretPositionIsAGuess() =>
        Assert.Equal(
            OverlayStyle.Popup,
            OverlayStylePolicy.StyleFor(SuggestionDisplayMode.Auto, Field("hello", 5, estimated: true), isCorrection: false));

    [Theory]
    [InlineData(SuggestionDisplayMode.Inline, OverlayStyle.Inline)]
    [InlineData(SuggestionDisplayMode.Popup, OverlayStyle.Popup)]
    public void ExplicitChoice_Wins(SuggestionDisplayMode mode, OverlayStyle expected) =>
        Assert.Equal(expected, OverlayStylePolicy.StyleFor(mode, Field("hello world", 5), isCorrection: false));
}
