using Cotabby.Core.Input;

namespace Cotabby.Core.Tests.Input;

/// <summary>
/// The duplicate-shortcut guard. A toggle on an accept key, for one, would never fire, because
/// the accept handler takes the key first.
/// </summary>
public sealed class ShortcutConflictTests
{
    private static readonly KeyBinding Space = new(0x20, KeyModifiers.None, "Space");
    private static readonly KeyBinding CtrlShiftSpace = new(0x20, KeyModifiers.Control | KeyModifiers.Shift, "Ctrl+Shift+Space");
    private static readonly KeyBinding ShiftBacktick = new(KeyBinding.VkBacktick, KeyModifiers.Shift, "~");

    private static ShortcutBindings Defaults(
        KeyBinding? toggle = null, params PerAppShortcutOverride[] overrides) =>
        new(KeyBinding.Tab, KeyBinding.Backtick, toggle ?? KeyBinding.Unbound, overrides);

    [Fact]
    public void Global_ReportsTheActionOwningTheKey() =>
        Assert.Equal("Accept Entire Suggestion",
            ShortcutConflicts.Global(Defaults(), ShortcutAction.Toggle, KeyBinding.Backtick));

    [Fact]
    public void Global_ExcludesTheActionBeingEdited() =>
        Assert.Null(ShortcutConflicts.Global(Defaults(), ShortcutAction.AcceptWord, KeyBinding.Tab));

    [Fact]
    public void Global_AllowsAnUnusedKey() =>
        Assert.Null(ShortcutConflicts.Global(Defaults(), ShortcutAction.Toggle, Space));

    [Fact]
    public void Global_IgnoresAnUnboundKey()
    {
        var bindings = Defaults() with { AcceptAll = KeyBinding.Unbound };

        Assert.Null(ShortcutConflicts.Global(bindings, ShortcutAction.Toggle, KeyBinding.Unbound));
    }

    [Fact]
    public void Global_TreatsModifierSetsAsDistinct() =>
        Assert.Null(ShortcutConflicts.Global(Defaults(), ShortcutAction.Toggle, ShiftBacktick));

    [Fact]
    public void PerApp_AllowsTheSameKeyAcrossDifferentApps()
    {
        var bindings = Defaults(overrides: new PerAppShortcutOverride { ProcessName = "notepad", AcceptWord = Space });

        Assert.Null(ShortcutConflicts.PerApp(bindings, "WindowsTerminal", ShortcutAction.AcceptWord, Space));
    }

    [Fact]
    public void PerApp_FlagsACollisionWithTheSameAppsOtherAction()
    {
        var bindings = Defaults(overrides: new PerAppShortcutOverride { ProcessName = "notepad", AcceptAll = Space });

        Assert.Equal("Accept Entire Suggestion",
            ShortcutConflicts.PerApp(bindings, "Notepad", ShortcutAction.AcceptWord, Space));
    }

    [Fact]
    public void PerApp_FlagsTheInheritedAcceptAllKey() =>
        Assert.Equal("Accept Entire Suggestion",
            ShortcutConflicts.PerApp(Defaults(), "notepad", ShortcutAction.AcceptWord, KeyBinding.Backtick));

    [Fact]
    public void PerApp_FlagsTheInheritedAcceptWordKey() =>
        Assert.Equal("Accept Word",
            ShortcutConflicts.PerApp(Defaults(), "notepad", ShortcutAction.AcceptAll, KeyBinding.Tab));

    [Fact]
    public void PerApp_FlagsTheGlobalToggle() =>
        Assert.Equal("Toggle Cotabby",
            ShortcutConflicts.PerApp(Defaults(CtrlShiftSpace), "notepad", ShortcutAction.AcceptWord, CtrlShiftSpace));

    [Fact]
    public void PerApp_AllowsRebindingTheSameActionToItsKey()
    {
        var bindings = Defaults(overrides: new PerAppShortcutOverride { ProcessName = "notepad", AcceptWord = Space });

        Assert.Null(ShortcutConflicts.PerApp(bindings, "notepad", ShortcutAction.AcceptWord, Space));
    }

    [Fact]
    public void PerApp_ADisabledOtherActionFreesItsKey()
    {
        var bindings = Defaults(overrides: new PerAppShortcutOverride { ProcessName = "notepad", AcceptAll = KeyBinding.Unbound });

        Assert.Null(ShortcutConflicts.PerApp(bindings, "notepad", ShortcutAction.AcceptWord, KeyBinding.Backtick));
    }

    [Fact]
    public void Normalize_TrimsDedupesIgnoringCaseAndSorts()
    {
        var normalized = ShortcutConflicts.Normalize(
        [
            new PerAppShortcutOverride { ProcessName = "  slack ", AcceptWord = Space },
            new PerAppShortcutOverride { ProcessName = "Notepad", AcceptWord = Space },
            new PerAppShortcutOverride { ProcessName = "   " },
            null,
            new PerAppShortcutOverride { ProcessName = "notepad", AcceptAll = KeyBinding.Unbound },
        ]);

        Assert.Equal(["notepad", "slack"], normalized.Select(o => o.ProcessName));
        Assert.Null(normalized[0].AcceptWord);
        Assert.Equal(KeyBinding.Unbound, normalized[0].AcceptAll);
    }
}
