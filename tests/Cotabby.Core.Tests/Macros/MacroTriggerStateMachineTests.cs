using Cotabby.Core.Input;
using Cotabby.Core.Macros;
using Outcome = Cotabby.Core.Macros.MacroTriggerStateMachine.Outcome;

namespace Cotabby.Core.Tests.Macros;

public class MacroTriggerStateMachineTests
{
    private static KeyboardEvent Ch(char c) => new()
    {
        Kind = KeyKind.Character,
        Character = c,
        HasNonShiftModifier = false,
        IsKeyDown = true,
    };

    private static KeyboardEvent Key(KeyKind kind) => new()
    {
        Kind = kind,
        Character = '\0',
        HasNonShiftModifier = false,
        IsKeyDown = true,
    };

    private static readonly KeyboardEvent CtrlChord = Key(KeyKind.Other) with { HasNonShiftModifier = true };

    private static MacroTriggerStateMachine Capturing(string query = "")
    {
        var sm = new MacroTriggerStateMachine();
        sm.Apply(Ch('/'), hasInsertableResult: false);
        Type(sm, query);
        return sm;
    }

    private static void Type(MacroTriggerStateMachine sm, string text)
    {
        foreach (char c in text) sm.Apply(Ch(c), hasInsertableResult: false);
    }

    [Fact]
    public void SlashAtBoundaryOpensCapture()
    {
        var sm = new MacroTriggerStateMachine();
        var outcome = sm.Apply(Ch('/'), hasInsertableResult: false);
        Assert.Equal(Outcome.Opened, outcome);
        Assert.False(sm.ConsumesKey);
        Assert.True(sm.IsActive);
        Assert.Equal("", sm.Query);
    }

    [Fact]
    public void SlashAfterWhitespaceOpensCapture()
    {
        var sm = new MacroTriggerStateMachine();
        Type(sm, "a ");
        Assert.Equal(Outcome.Opened, sm.Apply(Ch('/'), hasInsertableResult: false));
        Assert.True(sm.IsActive);
    }

    [Fact]
    public void SlashNotAtBoundaryNeverOpens()
    {
        var sm = new MacroTriggerStateMachine();
        Type(sm, "x");
        Assert.Equal(Outcome.None, sm.Apply(Ch('/'), hasInsertableResult: false));
        Assert.False(sm.IsActive);
    }

    [Fact]
    public void QueryCharactersExtendWithoutConsuming()
    {
        var sm = Capturing();
        foreach (char c in "5+5")
        {
            Assert.Equal(Outcome.QueryChanged, sm.Apply(Ch(c), hasInsertableResult: false));
            Assert.False(sm.ConsumesKey);
        }
        Assert.True(sm.IsActive);
        Assert.Equal("5+5", sm.Query);
    }

    [Fact]
    public void SlashWhileCapturingExtendsQueryAsDivision()
    {
        var sm = Capturing("10/2");
        Assert.True(sm.IsActive);
        Assert.Equal("10/2", sm.Query);
    }

    [Fact]
    public void SpaceTerminatesCaptureWithoutConsuming()
    {
        var sm = Capturing("5");
        Assert.Equal(Outcome.Cancel, sm.Apply(Ch(' '), hasInsertableResult: false));
        Assert.False(sm.ConsumesKey);
        Assert.False(sm.IsActive);
        Assert.Equal("", sm.Query);
    }

    [Fact]
    public void TabWithResultConsumesAndCommits()
    {
        var sm = Capturing("5+5");
        Assert.Equal(Outcome.Commit, sm.Apply(Key(KeyKind.Tab), hasInsertableResult: true));
        Assert.True(sm.ConsumesKey);
        Assert.False(sm.IsActive);
        Assert.Equal("", sm.Query);
        Assert.Equal("5+5", sm.LastCommittedQuery);
        Assert.Equal("/5+5".Length, sm.QueryAtCommitLength);
    }

    [Fact]
    public void TabWithoutResultPassesThrough()
    {
        var sm = Capturing();
        Assert.Equal(Outcome.Cancel, sm.Apply(Key(KeyKind.Tab), hasInsertableResult: false));
        Assert.False(sm.ConsumesKey);
        Assert.False(sm.IsActive);
    }

    [Fact]
    public void EscapeConsumesAndCancels()
    {
        var sm = Capturing();
        Assert.Equal(Outcome.Cancel, sm.Apply(Key(KeyKind.Escape), hasInsertableResult: true));
        Assert.True(sm.ConsumesKey);
    }

    [Fact]
    public void ConsumptionAppliesOnlyToTheKeyThatCausedIt()
    {
        var sm = Capturing();
        sm.Apply(Key(KeyKind.Escape), hasInsertableResult: false);
        Assert.Equal(Outcome.None, sm.Apply(Ch('a'), hasInsertableResult: false));
        Assert.False(sm.ConsumesKey);
    }

    [Fact]
    public void BackspaceOnEmptyQueryCancelsWithoutConsuming()
    {
        var sm = Capturing();
        Assert.Equal(Outcome.Cancel, sm.Apply(Key(KeyKind.Backspace), hasInsertableResult: false));
        Assert.False(sm.ConsumesKey);
        Assert.False(sm.IsActive);
    }

    [Fact]
    public void BackspaceShortensQuery()
    {
        var sm = Capturing("ab");
        Assert.Equal(Outcome.QueryChanged, sm.Apply(Key(KeyKind.Backspace), hasInsertableResult: false));
        Assert.Equal("a", sm.Query);
    }

    [Fact]
    public void ResetReturnsToIdleAndRestoresBoundary()
    {
        var sm = Capturing("5");
        sm.Apply(Key(KeyKind.Tab), hasInsertableResult: true);
        Type(sm, "x");

        sm.Reset();

        Assert.False(sm.IsActive);
        Assert.Equal("", sm.Query);
        Assert.Equal("", sm.LastCommittedQuery);
        // A cleared machine has no boundary memory, so the next slash opens as at the start of a field.
        Assert.Equal(Outcome.Opened, sm.Apply(Ch('/'), hasInsertableResult: false));
    }

    public static TheoryData<KeyboardEvent> NonCharacterKeys => new()
    {
        Key(KeyKind.Backspace),
        Key(KeyKind.Tab),
        Key(KeyKind.Escape),
        Key(KeyKind.Arrow),
        Key(KeyKind.Enter),
        Key(KeyKind.Delete),
        CtrlChord,
    };

    [Theory]
    [MemberData(nameof(NonCharacterKeys))]
    public void NonCharacterKeysWhileIdleAreIgnoredAndClearBoundaryMemory(KeyboardEvent key)
    {
        var sm = new MacroTriggerStateMachine();
        Type(sm, "x");

        Assert.Equal(Outcome.None, sm.Apply(key, hasInsertableResult: true));
        Assert.False(sm.ConsumesKey);
        // The machine forgot the preceding x, so the next slash is evaluated as a fresh boundary.
        Assert.Equal(Outcome.Opened, sm.Apply(Ch('/'), hasInsertableResult: false));
    }

    [Fact]
    public void TerminatorIsRememberedAsTheBoundaryForTheNextSlash()
    {
        // A whitespace terminator leaves the machine at a boundary, so "/5 /" reopens immediately.
        var afterSpace = Capturing("5");
        afterSpace.Apply(Ch(' '), hasInsertableResult: false);
        Assert.Equal(Outcome.Opened, afterSpace.Apply(Ch('/'), hasInsertableResult: false));

        // A punctuation terminator does not, so "/5!/" stays closed like any mid-word slash.
        var afterPunctuation = Capturing("5");
        Assert.Equal(Outcome.Cancel, afterPunctuation.Apply(Ch('!'), hasInsertableResult: false));
        Assert.Equal(Outcome.None, afterPunctuation.Apply(Ch('/'), hasInsertableResult: false));
        Assert.False(afterPunctuation.IsActive);
    }

    [Fact]
    public void QueryGrammarAcceptsOperatorsAndRejectsTerminators()
    {
        foreach (char c in "aZ09+-*/^%(),.=>")
        {
            Assert.True(MacroQueryGrammar.Extends(c), $"{c} should extend the query");
        }
        foreach (char c in " \t!?:;<'\"")
        {
            Assert.False(MacroQueryGrammar.Extends(c), $"{c} should end capture");
        }
    }

    public static TheoryData<KeyboardEvent> DismissingKeys => new()
    {
        Key(KeyKind.Arrow),
        Key(KeyKind.Enter),
        Key(KeyKind.Delete),
        CtrlChord,
        Ch('a') with { HasNonShiftModifier = true },
        Key(KeyKind.Backspace) with { HasNonShiftModifier = true },
    };

    [Theory]
    [MemberData(nameof(DismissingKeys))]
    public void NavigationAndChordsWhileCapturingCancelWithoutConsuming(KeyboardEvent key)
    {
        var sm = Capturing("5");

        Assert.Equal(Outcome.Cancel, sm.Apply(key, hasInsertableResult: true));
        Assert.False(sm.ConsumesKey);
        Assert.False(sm.IsActive);
    }

    [Fact]
    public void KeyUpEventsAreIgnored()
    {
        var sm = new MacroTriggerStateMachine();
        Assert.Equal(Outcome.None, sm.Apply(Ch('/') with { IsKeyDown = false }, hasInsertableResult: false));
        Assert.False(sm.IsActive);

        sm = Capturing("5");
        Assert.Equal(Outcome.None, sm.Apply(Key(KeyKind.Escape) with { IsKeyDown = false }, hasInsertableResult: true));
        Assert.False(sm.ConsumesKey);
        Assert.Equal("5", sm.Query);
    }

    [Fact]
    public void BareModifierPressKeepsCaptureAndBoundaryMemory()
    {
        // Shift arrives as its own Other key before the shifted character.
        var sm = Capturing("5");
        Assert.Equal(Outcome.None, sm.Apply(Key(KeyKind.Other), hasInsertableResult: false));
        Type(sm, "+5");
        Assert.Equal("5+5", sm.Query);

        var idle = new MacroTriggerStateMachine();
        Type(idle, "x");
        idle.Apply(Key(KeyKind.Other), hasInsertableResult: false);
        Assert.Equal(Outcome.None, idle.Apply(Ch('/'), hasInsertableResult: false));
    }
}
