using Cotabby.Core.Emoji;
using Cotabby.Core.Input;
using Xunit;

namespace Cotabby.Core.Tests;

public class EmojiTriggerStateMachineTests
{
    private static KeyboardEvent Ch(char c) => new()
    {
        Kind = KeyKind.Character,
        Character = c,
        HasNonShiftModifier = false,
        IsKeyDown = true,
    };

    private static KeyboardEvent Key(KeyKind k) => new()
    {
        Kind = k,
        Character = '\0',
        HasNonShiftModifier = false,
        IsKeyDown = true,
    };

    [Fact]
    public void ColonStartsTrigger()
    {
        var sm = new EmojiTriggerStateMachine();
        var o = sm.Apply(Ch(':'));
        Assert.Equal(EmojiTriggerStateMachine.Outcome.QueryChanged, o);
        Assert.True(sm.IsActive);
        Assert.Equal("", sm.Query);
    }

    [Fact]
    public void AlphabeticExtendsQuery()
    {
        var sm = new EmojiTriggerStateMachine();
        sm.Apply(Ch(':'));
        sm.Apply(Ch('s'));
        sm.Apply(Ch('m'));
        sm.Apply(Ch('i'));
        sm.Apply(Ch('l'));
        sm.Apply(Ch('e'));
        Assert.Equal("smile", sm.Query);
        Assert.True(sm.IsActive);
    }

    [Fact]
    public void TrailingColonCommits()
    {
        var sm = new EmojiTriggerStateMachine();
        sm.Apply(Ch(':'));
        sm.Apply(Ch('s'));
        sm.Apply(Ch('m'));
        sm.Apply(Ch('i'));
        sm.Apply(Ch('l'));
        sm.Apply(Ch('e'));
        var o = sm.Apply(Ch(':'));
        Assert.Equal(EmojiTriggerStateMachine.Outcome.Commit, o);
        Assert.Equal("smile", sm.LastCommittedQuery);
        Assert.Equal(":smile:".Length, sm.QueryAtCommitLength);
        Assert.False(sm.IsActive);
        Assert.Equal("", sm.Query);
    }

    [Fact]
    public void EmptyTrailingColonCancels()
    {
        var sm = new EmojiTriggerStateMachine();
        sm.Apply(Ch(':'));
        var o = sm.Apply(Ch(':'));
        Assert.Equal(EmojiTriggerStateMachine.Outcome.Cancel, o);
        Assert.False(sm.IsActive);
    }

    [Fact]
    public void SpaceCancels()
    {
        var sm = new EmojiTriggerStateMachine();
        sm.Apply(Ch(':'));
        sm.Apply(Ch('s'));
        var o = sm.Apply(Ch(' '));
        Assert.Equal(EmojiTriggerStateMachine.Outcome.Cancel, o);
    }

    [Fact]
    public void BackspaceShrinksQuery()
    {
        var sm = new EmojiTriggerStateMachine();
        sm.Apply(Ch(':'));
        sm.Apply(Ch('s'));
        sm.Apply(Ch('m'));
        var o = sm.Apply(Key(KeyKind.Backspace));
        Assert.Equal(EmojiTriggerStateMachine.Outcome.QueryChanged, o);
        Assert.Equal("s", sm.Query);
    }

    [Fact]
    public void BackspaceWhenQueryEmptyCancels()
    {
        var sm = new EmojiTriggerStateMachine();
        sm.Apply(Ch(':'));
        var o = sm.Apply(Key(KeyKind.Backspace));
        Assert.Equal(EmojiTriggerStateMachine.Outcome.Cancel, o);
    }

    [Fact]
    public void EscapeCancels()
    {
        var sm = new EmojiTriggerStateMachine();
        sm.Apply(Ch(':'));
        sm.Apply(Ch('s'));
        var o = sm.Apply(Key(KeyKind.Escape));
        Assert.Equal(EmojiTriggerStateMachine.Outcome.Cancel, o);
    }

    [Fact]
    public void TabCancels()
    {
        var sm = new EmojiTriggerStateMachine();
        sm.Apply(Ch(':'));
        sm.Apply(Ch('s'));
        var o = sm.Apply(Key(KeyKind.Tab));
        Assert.Equal(EmojiTriggerStateMachine.Outcome.Cancel, o);
    }

    [Fact]
    public void DigitsAndPlusMinusUnderscoreExtend()
    {
        var sm = new EmojiTriggerStateMachine();
        sm.Apply(Ch(':'));
        sm.Apply(Ch('+'));
        sm.Apply(Ch('1'));
        Assert.Equal("+1", sm.Query);

        sm.Reset();
        sm.Apply(Ch(':'));
        sm.Apply(Ch('s'));
        sm.Apply(Ch('-'));
        sm.Apply(Ch('1'));
        Assert.Equal("s-1", sm.Query);
    }

    [Fact]
    public void ModifierKeysIgnored()
    {
        var sm = new EmojiTriggerStateMachine();
        sm.Apply(Ch(':'));
        var o = sm.Apply(new KeyboardEvent
        {
            Kind = KeyKind.Character,
            Character = 's',
            HasNonShiftModifier = true,
            IsKeyDown = true,
        });
        Assert.Equal(EmojiTriggerStateMachine.Outcome.None, o);
        Assert.True(sm.IsActive);
        Assert.Equal("", sm.Query);
    }
}
