using Cotabby.Core.Input;

namespace Cotabby.Core.Tests.Input;

public sealed class ShortcutResolverTests
{
    private const string App = "notepad";

    private static readonly KeyBinding GlobalWord = KeyBinding.Tab;
    private static readonly KeyBinding GlobalAll = KeyBinding.Backtick;
    private static readonly KeyBinding ShiftSpace = new(0x20, KeyModifiers.Shift, "Shift+Space");
    private static readonly KeyBinding CtrlEnter = new(0x0D, KeyModifiers.Control, "Ctrl+Enter");

    private static PerAppShortcutOverride Override(
        KeyBinding? acceptWord = null, KeyBinding? acceptAll = null, string process = App) =>
        new() { ProcessName = process, AcceptWord = acceptWord, AcceptAll = acceptAll };

    private static KeyBinding Word(string? process, params PerAppShortcutOverride[] overrides) =>
        ShortcutResolver.AcceptWord(process, overrides, GlobalWord);

    private static KeyBinding All(string? process, params PerAppShortcutOverride[] overrides) =>
        ShortcutResolver.AcceptAll(process, overrides, GlobalAll);

    [Fact]
    public void WithNoOverride_BothActionsUseTheGlobalBinding()
    {
        Assert.Equal(GlobalWord, Word(App));
        Assert.Equal(GlobalAll, All(App));
    }

    [Fact]
    public void AnOverride_ReplacesOnlyItsOwnAction()
    {
        Assert.Equal(ShiftSpace, Word(App, Override(acceptWord: ShiftSpace)));
        Assert.Equal(GlobalAll, All(App, Override(acceptWord: ShiftSpace)));
        Assert.Equal(CtrlEnter, All(App, Override(acceptAll: CtrlEnter)));
        Assert.Equal(GlobalWord, Word(App, Override(acceptAll: CtrlEnter)));
    }

    [Fact]
    public void AnOverrideWithNoBindings_UsesTheGlobalBindingForBothActions()
    {
        Assert.Equal(GlobalWord, Word(App, Override()));
        Assert.Equal(GlobalAll, All(App, Override()));
    }

    [Theory]
    [InlineData("chrome")]
    [InlineData("")]
    [InlineData(null)]
    public void AnOverride_DoesNotApplyToAnotherOrUnknownApp(string? process)
    {
        var entry = Override(ShiftSpace, CtrlEnter);

        Assert.Equal(GlobalWord, Word(process, entry));
        Assert.Equal(GlobalAll, All(process, entry));
    }

    [Fact]
    public void AnEmptyProcessName_NeverMatchesAnOverrideStoredWithAnEmptyName()
    {
        var entry = Override(ShiftSpace, CtrlEnter, process: "");

        Assert.Equal(GlobalWord, Word("", entry));
        Assert.Equal(GlobalAll, All("", entry));
    }

    [Fact]
    public void AnOverride_MatchesItsProcessNameIgnoringCase() =>
        Assert.Equal(ShiftSpace, Word("NOTEPAD", Override(acceptWord: ShiftSpace)));

    [Fact]
    public void AnUnboundOverride_DisablesTheActionInsteadOfInheriting()
    {
        Assert.Equal(KeyBinding.Unbound, Word(App, Override(acceptWord: KeyBinding.Unbound)));
        Assert.Equal(KeyBinding.Unbound, All(App, Override(acceptAll: KeyBinding.Unbound)));
    }

    [Fact]
    public void TheFirstMatchingOverrideWins_EvenWhenItLacksTheRequestedBinding()
    {
        var first = Override(acceptAll: CtrlEnter);
        var duplicate = Override(ShiftSpace, GlobalWord);

        Assert.Equal(GlobalWord, Word(App, first, duplicate));
        Assert.Equal(CtrlEnter, All(App, first, duplicate));
    }

    [Fact]
    public void OverridesAcceptAll_TrueForARecordedOrUnboundAcceptAllOnly()
    {
        Assert.True(ShortcutResolver.OverridesAcceptAll(App, [Override(acceptAll: CtrlEnter)]));
        Assert.True(ShortcutResolver.OverridesAcceptAll(App, [Override(acceptAll: KeyBinding.Unbound)]));
        Assert.False(ShortcutResolver.OverridesAcceptAll(App, [Override(acceptWord: ShiftSpace)]));
        Assert.False(ShortcutResolver.OverridesAcceptAll("chrome", [Override(acceptAll: CtrlEnter)]));
        Assert.False(ShortcutResolver.OverridesAcceptAll(App, []));
    }
}
