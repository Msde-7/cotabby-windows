using Cotabby.Win32.Insertion;

namespace Cotabby.Inference.Tests;

public class SyntheticKeysTests
{
    private const ushort LeftCtrl = 0xA2;
    private const ushort RightShift = 0xA1;
    private const ushort LeftWin = 0x5B;
    private const ushort Ctrl = 0x11;
    private const ushort V = 0x56;
    private const ushort Backspace = 0x08;

    private static readonly ushort[] NoneHeld = [];

    private static SyntheticKey Down(ushort vk) => SyntheticKey.Down(vk);
    private static SyntheticKey Up(ushort vk) => SyntheticKey.Up(vk);
    private static SyntheticKey CharDown(char c) => new(0, c, KeyUp: false);
    private static SyntheticKey CharUp(char c) => new(0, c, KeyUp: true);

    [Fact]
    public void Modifiers_CoverBothSidesOfShiftCtrlAltAndWin()
    {
        Assert.Equal(new ushort[] { 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5, 0x5B, 0x5C }, SyntheticKeys.Modifiers);
    }

    [Fact]
    public void Text_IsOneUnicodePressPerUtf16Unit()
    {
        Assert.Equal(
            [CharDown('h'), CharUp('h'), CharDown('\uD83D'), CharUp('\uD83D'), CharDown('\uDE00'), CharUp('\uDE00')],
            SyntheticKeys.Text(NoneHeld, "h\uD83D\uDE00"));
    }

    [Fact]
    public void Text_ReleasesHeldModifiersFirstAndNeverPressesThemAgain()
    {
        Assert.Equal(
            [Up(LeftCtrl), Up(RightShift), CharDown('o'), CharUp('o'), CharDown('k'), CharUp('k')],
            SyntheticKeys.Text([LeftCtrl, RightShift], "ok"));
    }

    [Fact]
    public void Backspaces_ReleaseHeldModifiersSoCtrlDoesNotDeleteAWord()
    {
        Assert.Equal(
            [Up(LeftCtrl), Down(Backspace), Up(Backspace), Down(Backspace), Up(Backspace)],
            SyntheticKeys.Backspaces([LeftCtrl], 2));
        Assert.Equal([Down(Backspace), Up(Backspace)], SyntheticKeys.Backspaces(NoneHeld, 1));
    }

    [Fact]
    public void Paste_ReleasesHeldModifiersBeforeCtrlV()
    {
        Assert.Equal(
            [Up(RightShift), Up(LeftCtrl), Down(Ctrl), Down(V), Up(V), Up(Ctrl)],
            SyntheticKeys.Paste([RightShift, LeftCtrl]));
        Assert.Equal([Down(Ctrl), Down(V), Up(V), Up(Ctrl)], SyntheticKeys.Paste(NoneHeld));
    }

    [Fact]
    public void ReleasingAltOrWin_PressesAnInertKeyFirstSoNoMenuOpens()
    {
        const ushort inert = 0xE8;

        Assert.Equal(
            [Down(inert), Up(inert), Up(LeftWin), Down(Ctrl), Down(V), Up(V), Up(Ctrl)],
            SyntheticKeys.Paste([LeftWin]));
    }

    [Theory]
    [InlineData("word", true)]
    [InlineData("two words", true)]
    [InlineData("line\nbreak", false)]
    [InlineData("line\rbreak", false)]
    [InlineData("line\r\nbreak", false)]
    [InlineData("line\u2028break", false)]
    [InlineData("paragraph\u2029break", false)]
    [InlineData("next\u0085line", false)]
    [InlineData("form\ffeed", false)]
    [InlineData("vertical\vtab", false)]
    public void CanType_OnlyTextWithNoLineBreak(string text, bool expected)
    {
        Assert.Equal(expected, SyntheticKeys.CanType(text));
    }

    [Fact]
    public void CanType_OnlyUpToTheLengthLimit()
    {
        Assert.True(SyntheticKeys.CanType(new string('a', SyntheticKeys.MaxTypedChars)));
        Assert.False(SyntheticKeys.CanType(new string('a', SyntheticKeys.MaxTypedChars + 1)));
    }
}
