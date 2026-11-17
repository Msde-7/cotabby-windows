using Cotabby.Win32.Input;

namespace Cotabby.Inference.Tests;

public class HookWatchdogTests
{
    [Fact]
    public void KeysHeardByTheHook_AreFine()
    {
        var watchdog = new HookWatchdog();

        Assert.False(watchdog.Check(callbacks: 10, rawKeys: 10));
        Assert.False(watchdog.Check(callbacks: 20, rawKeys: 20));
    }

    [Fact]
    public void KeysTheHookNeverHeard_MeanItWasDropped()
    {
        var watchdog = new HookWatchdog();
        watchdog.Check(callbacks: 10, rawKeys: 10);

        Assert.True(watchdog.Check(callbacks: 10, rawKeys: 14));
    }

    [Fact]
    public void AnIdleKeyboard_IsNotADroppedHook()
    {
        var watchdog = new HookWatchdog();
        watchdog.Check(callbacks: 10, rawKeys: 10);

        Assert.False(watchdog.Check(callbacks: 10, rawKeys: 10));
    }

    [Fact]
    public void OneKeyWhoseCallbackLandedInThePreviousPeriod_IsNotADrop()
    {
        var watchdog = new HookWatchdog();
        watchdog.Check(callbacks: 11, rawKeys: 10);

        Assert.False(watchdog.Check(callbacks: 11, rawKeys: 11));
    }

    [Fact]
    public void AfterAReinstall_TheHookMustBeHeardBeforeTheNext()
    {
        var watchdog = new HookWatchdog();
        watchdog.Check(callbacks: 10, rawKeys: 10);
        Assert.True(watchdog.Check(callbacks: 10, rawKeys: 20));

        // Still silent, as while an elevated window has focus: no second reinstall.
        Assert.False(watchdog.Check(callbacks: 10, rawKeys: 30));
        Assert.False(watchdog.Check(callbacks: 10, rawKeys: 40));

        // Heard again, then silent again: a new stretch earns a new reinstall.
        Assert.False(watchdog.Check(callbacks: 15, rawKeys: 45));
        Assert.True(watchdog.Check(callbacks: 15, rawKeys: 55));
    }
}
