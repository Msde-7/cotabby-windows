using Cotabby.Core.Input;
using Cotabby.Core.Tests.Suggestions;

namespace Cotabby.Core.Tests.Input;

public sealed class KeyBindingTests
{
    private static readonly KeyBinding CtrlSpace = new(0x20, KeyModifiers.Control, "Ctrl+Space");
    private static readonly KeyboardEvent CtrlSpacePress =
        Keys.Special(KeyKind.Other, 0x20) with { Modifiers = KeyModifiers.Control, HasNonShiftModifier = true };

    [Fact]
    public void Matches_TheSameKeyWithExactlyTheSameModifiers()
    {
        Assert.True(KeyBinding.Tab.Matches(Keys.Tab()));
        Assert.True(CtrlSpace.Matches(CtrlSpacePress));
    }

    [Fact]
    public void Matches_FalseForAnotherKeyOrOtherModifiers()
    {
        Assert.False(KeyBinding.Tab.Matches(Keys.Char('a')));
        Assert.False(KeyBinding.Tab.Matches(Keys.Tab() with { Modifiers = KeyModifiers.Shift }));
        Assert.False(CtrlSpace.Matches(CtrlSpacePress with { Modifiers = KeyModifiers.Control | KeyModifiers.Shift }));
        Assert.False(CtrlSpace.Matches(Keys.Special(KeyKind.Character, 0x20)));
    }

    [Fact]
    public void Matches_AnUnboundBindingMatchesNothing()
    {
        Assert.False(KeyBinding.Unbound.IsBound);
        Assert.False(KeyBinding.Unbound.Matches(Keys.Special(KeyKind.Other)));
    }

    [Fact]
    public void Normalize_TheCommitKeyBecomesAPlainTab()
    {
        var normalized = InlineCommitKey.Normalize(CtrlSpacePress, CtrlSpace);

        Assert.Equal(CtrlSpacePress with { Kind = KeyKind.Tab, HasNonShiftModifier = false }, normalized);
    }

    [Fact]
    public void Normalize_TabStaysTabWhenItIsTheCommitKey() =>
        Assert.Equal(Keys.Tab(), InlineCommitKey.Normalize(Keys.Tab(), KeyBinding.Tab));

    [Fact]
    public void Normalize_TabThatIsNotTheCommitKeyBecomesNavigation() =>
        Assert.Equal(Keys.Tab() with { Kind = KeyKind.Arrow }, InlineCommitKey.Normalize(Keys.Tab(), CtrlSpace));

    [Fact]
    public void Normalize_OtherKeysPassThroughUnchanged()
    {
        var press = Keys.Char('a');

        Assert.Same(press, InlineCommitKey.Normalize(press, CtrlSpace));
    }
}
