using Cotabby.Win32.Input;

namespace Cotabby.Inference.Tests;

public class HeldKeysTests
{
    private const uint Tab = 0x09;
    private const uint A = 0x41;
    private const uint L = 0x4C;
    private const uint LeftWin = 0x5B;

    [Fact]
    public void AutoRepeat_IsARepeat()
    {
        var keys = new HeldKeys();
        Assert.False(keys.Down(Tab, time: 1000));
        Assert.True(keys.Down(Tab, time: 1500));
        Assert.True(keys.Down(Tab, time: 1533));
    }

    [Fact]
    public void ASecondPressAfterARelease_IsNotARepeat()
    {
        var keys = new HeldKeys();
        keys.Down(Tab, 1000);
        keys.Up(Tab);
        Assert.False(keys.Down(Tab, 1100));
    }

    [Fact]
    public void AKeyWhoseReleaseWasMissed_PressesFreshAfterAnotherKey()
    {
        // Win+L locks the PC, and the releases happen on the secure desktop where the hook hears nothing.
        var keys = new HeldKeys();
        keys.Down(LeftWin, 1000);
        keys.Down(L, 1050);
        Assert.False(keys.Down(Tab, 60_000));
        Assert.False(keys.Down(L, 61_000));
        Assert.False(keys.Down(LeftWin, 62_000));
    }

    [Fact]
    public void AKeyWhoseReleaseWasMissed_PressesFreshAfterAPause()
    {
        var keys = new HeldKeys();
        keys.Down(Tab, 1000);
        Assert.False(keys.Down(Tab, 60_000));
        Assert.True(keys.Down(Tab, 60_400));
    }

    [Fact]
    public void AnotherKeyStopsTheRepeat()
    {
        var keys = new HeldKeys();
        keys.Down(Tab, 1000);
        keys.Down(A, 1100);
        // Only the last key pressed repeats, so this press of the held Tab is a new one.
        Assert.False(keys.Down(Tab, 1200));
    }

    [Fact]
    public void HeldIsTrackedUntilRelease()
    {
        var keys = new HeldKeys();
        keys.Down(LeftWin, 1000);
        Assert.True(keys.IsHeld(LeftWin));
        keys.Up(LeftWin);
        Assert.False(keys.IsHeld(LeftWin));
    }

    [Fact]
    public void TheTickCountWrappingAroundStillCountsAsShort()
    {
        var keys = new HeldKeys();
        keys.Down(Tab, uint.MaxValue - 100);
        Assert.True(keys.Down(Tab, 200));
    }
}
