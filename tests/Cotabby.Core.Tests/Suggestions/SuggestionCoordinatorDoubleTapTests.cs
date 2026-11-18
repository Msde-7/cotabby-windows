using Cotabby.Core.Input;
using Cotabby.Core.Suggestions;

namespace Cotabby.Core.Tests.Suggestions;

/// <summary>
/// The first accept-word press takes one word, a quick second press on the same suggestion takes
/// the rest, and anything else keeps word-by-word acceptance. Press timing comes from a manual clock.
/// </summary>
public sealed class SuggestionCoordinatorDoubleTapTests : CoordinatorHarness
{
    private static readonly TimeSpan Quick = TimeSpan.FromMilliseconds(100);

    private readonly ManualClock _clock;

    public SuggestionCoordinatorDoubleTapTests() : this(new ManualClock())
    {
    }

    private SuggestionCoordinatorDoubleTapTests(ManualClock clock) : base(clock: clock) => _clock = clock;

    private async Task Ready(bool doubleTap = true, string completion = " world again tomorrow",
        params PerAppShortcutOverride[] overrides)
    {
        Coordinator.Settings = SuggestionSettings.Default with
        {
            DoubleTapAcceptsAll = doubleTap,
            AcceptAll = doubleTap ? KeyBinding.Unbound : KeyBinding.Backtick,
            ShortcutOverrides = overrides,
        };
        // Nothing follows the suggestion, so whatever shows after it is used up came from it.
        Engine.Completion = request => request.Prefix == "Hello" ? completion : "";
        Type("Hello");
        await Shown(completion);
    }

    private async Task FirstWord(string remaining = " again tomorrow")
    {
        Assert.True(Hook.Press(Keys.Tab()));
        await Shown(remaining);
    }

    private Task Inserted(string text) =>
        Eventually(() => Focus.Current!.Text == text, $"the field reads \"{text}\"");

    [Fact]
    public async Task AQuickSecondPress_AcceptsTheRest()
    {
        await Ready();
        await FirstWord();

        _clock.Advance(Quick);
        Assert.True(Hook.Press(Keys.Tab()));

        await Inserted("Hello world again tomorrow");
        await Settled();
        Assert.Equal([" world", " again tomorrow"], Focus.Insertions);
        Assert.Equal("", Overlay.Text);
    }

    [Fact]
    public async Task WithTheSettingOff_EachPressTakesAWord()
    {
        await Ready(doubleTap: false);
        await FirstWord();

        _clock.Advance(Quick);
        Assert.True(Hook.Press(Keys.Tab()));

        await Shown(" tomorrow");
        await Inserted("Hello world again");
    }

    [Fact]
    public async Task ASecondPressAfterTheWindow_TakesOnlyTheNextWord()
    {
        await Ready();
        await FirstWord();

        _clock.Advance(DoubleTapAcceptanceState.Window + TimeSpan.FromMilliseconds(50));
        Assert.True(Hook.Press(Keys.Tab()));

        await Shown(" tomorrow");
    }

    [Fact]
    public async Task AClockThatGoesBackwards_IsNotADoubleTap()
    {
        await Ready();
        await FirstWord();

        _clock.Advance(-Quick);
        Assert.True(Hook.Press(Keys.Tab()));

        await Shown(" tomorrow");
    }

    [Fact]
    public async Task AKeyBetweenThePresses_CancelsTheDoubleTap()
    {
        await Ready();
        await FirstWord();

        // F5 changes nothing in the field, so the suggestion stays, but the presses are no longer a pair.
        Hook.Press(Keys.Special(KeyKind.Other, virtualKey: 0x74));
        _clock.Advance(Quick);
        Assert.True(Hook.Press(Keys.Tab()));

        await Shown(" tomorrow");
    }

    [Fact]
    public async Task AModifierBetweenThePresses_KeepsTheDoubleTap()
    {
        await Ready();
        await FirstWord();

        Hook.Press(Keys.Special(KeyKind.Other, virtualKey: 0xA0));
        _clock.Advance(Quick);
        Assert.True(Hook.Press(Keys.Tab()));

        await Inserted("Hello world again tomorrow");
    }

    [Fact]
    public async Task AHeldKey_KeepsTakingWordsWithoutDoubleTapping()
    {
        await Ready(completion: " world again tomorrow morning");
        await FirstWord(" again tomorrow morning");

        // A repeat is one long press. It takes a word and neither completes nor arms a pair.
        _clock.Advance(TimeSpan.FromMilliseconds(50));
        Assert.True(Hook.Press(Keys.Tab(repeat: true)));
        await Shown(" tomorrow morning");

        _clock.Advance(TimeSpan.FromMilliseconds(50));
        Assert.True(Hook.Press(Keys.Tab()));
        await Shown(" morning");
    }

    [Fact]
    public async Task AnAppWithItsOwnAcceptAllBinding_KeepsWordByWord()
    {
        await Ready(overrides: new PerAppShortcutOverride { ProcessName = "notepad", AcceptAll = KeyBinding.Unbound });
        await FirstWord();

        _clock.Advance(Quick);
        Assert.True(Hook.Press(Keys.Tab()));

        await Shown(" tomorrow");
    }

    [Fact]
    public async Task OtherAppsAcceptAllBindings_LeaveThisAppAlone()
    {
        await Ready(overrides: new PerAppShortcutOverride { ProcessName = "WindowsTerminal", AcceptAll = KeyBinding.Unbound });
        await FirstWord();

        _clock.Advance(Quick);
        Assert.True(Hook.Press(Keys.Tab()));

        await Inserted("Hello world again tomorrow");
    }
}
