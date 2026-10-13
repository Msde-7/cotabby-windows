using Cotabby.Core.Focus;

namespace Cotabby.Core.Tests.Focus;

public sealed class FieldShapeTests
{
    private static readonly ScreenRect Caret = new(10, 10, 1, 20);

    [Fact]
    public void DocumentControl_IsNeverSingleLine() =>
        Assert.False(FieldShape.IsSingleLine(isEditControl: false, new ScreenRect(0, 0, 300, 24), Caret));

    [Theory]
    [InlineData(24, true)]
    [InlineData(50, true)]
    [InlineData(51, false)]
    [InlineData(600, false)]
    public void EditControl_IsSingleLineOnlyWhenAboutOneLineTall(double fieldHeight, bool expected) =>
        Assert.Equal(expected, FieldShape.IsSingleLine(isEditControl: true, new ScreenRect(0, 0, 300, fieldHeight), Caret));

    [Fact]
    public void EditControl_WithoutACaretRectFallsBackToItsType() =>
        Assert.True(FieldShape.IsSingleLine(isEditControl: true, new ScreenRect(0, 0, 300, 600), ScreenRect.Empty));
}
