using Cotabby.Win32.Clipboard;

namespace Cotabby.Inference.Tests;

/// <summary>The parts of <see cref="ClipboardSwap"/> that do not touch the clipboard.</summary>
public class ClipboardSwapTests
{
    [Theory]
    [InlineData("one\ntwo", "one\r\ntwo")]
    [InlineData("one\r\ntwo\rthree\n", "one\r\ntwo\r\nthree\r\n")]
    [InlineData("no break", "no break")]
    public void LineBreaks_BecomeCrLf(string text, string expected) =>
        Assert.Equal(expected, ClipboardSwap.WithCrLf(text));

    [Theory]
    [InlineData(0x2028)] // line separator
    [InlineData(0x2029)] // paragraph separator
    [InlineData(0x0085)] // next line
    [InlineData(0x000C)] // form feed
    [InlineData(0x000B)] // vertical tab
    public void OtherSeparators_AreLeftAsTheyAre(int separator)
    {
        string text = "a" + (char)separator + "b";
        Assert.Equal(text, ClipboardSwap.WithCrLf(text));
        Assert.Equal(text + "\r\nc", ClipboardSwap.WithCrLf(text + "\nc"));
    }
}
