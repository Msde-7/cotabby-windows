using Cotabby.Core.Emoji;
using Cotabby.Core.Input;
using Output = Cotabby.Core.Emoji.EmojiTriggerStateMachine.Output;

namespace Cotabby.Core.Tests.Emoji;

/// <summary>
/// Locks down the capture lifecycle and the consumption policy. Query characters always pass
/// through to the field, while navigation, commit and Escape are consumed only when there are
/// matches to act on.
/// </summary>
public class EmojiTriggerStateMachineTests
{
    private static Output Type(EmojiTriggerStateMachine machine, string text, int matchCount = 0)
    {
        Output last = default;
        foreach (char character in text) last = machine.Reduce(EmojiTriggerInput.Typed(character), matchCount);
        return last;
    }

    private static EmojiTriggerStateMachine Capturing(string query = "", int matchCount = 0)
    {
        var machine = new EmojiTriggerStateMachine();
        Type(machine, ":" + query, matchCount);
        return machine;
    }

    private static KeyboardEvent Key(KeyKind kind, char character = '\0', bool down = true, bool modifier = false) =>
        new()
        {
            Kind = kind,
            Character = character,
            IsKeyDown = down,
            Modifiers = modifier ? KeyModifiers.Control : KeyModifiers.None,
        };

    // Trigger boundary

    [Fact]
    public void Trigger_AtStartOfField_Opens()
    {
        var sut = new EmojiTriggerStateMachine();

        Assert.Equal(new Output(EmojiTriggerAction.Open, false), Type(sut, ":"));
        Assert.True(sut.IsCapturing);
        Assert.Equal("", sut.Query);
    }

    [Fact]
    public void Trigger_AfterWhitespace_Opens()
    {
        var sut = new EmojiTriggerStateMachine();

        Assert.Equal(EmojiTriggerAction.Open, Type(sut, "hi :").Action);
        Assert.True(sut.IsCapturing);
    }

    [Fact]
    public void Trigger_AfterLetter_DoesNotOpen()
    {
        var sut = new EmojiTriggerStateMachine();

        Assert.Equal(Output.Ignored, Type(sut, "a:"));
        Assert.False(sut.IsCapturing);
    }

    [Fact]
    public void DoubleColon_DoesNotOpen()
    {
        var sut = new EmojiTriggerStateMachine();

        Assert.Equal(Output.Ignored, Type(sut, "a:"));
        Assert.Equal(Output.Ignored, Type(sut, ":"));
        Assert.False(sut.IsCapturing);
    }

    // Query editing, never consumed

    [Fact]
    public void NameCharacters_ExtendQueryWithoutConsuming()
    {
        var sut = Capturing();

        Assert.Equal(new Output(EmojiTriggerAction.UpdateQuery("s"), false), Type(sut, "s"));
        Assert.Equal(new Output(EmojiTriggerAction.UpdateQuery("sm"), false), Type(sut, "m"));
        Assert.Equal("sm", sut.Query);
    }

    [Fact]
    public void AliasPunctuationExtendsQuery()
    {
        var sut = Capturing();

        Assert.Equal(EmojiTriggerAction.UpdateQuery("+"), Type(sut, "+").Action);
        Assert.Equal(EmojiTriggerAction.UpdateQuery("+1"), Type(sut, "1").Action);
    }

    [Fact]
    public void Backspace_ShortensQuery()
    {
        var sut = Capturing("sm");

        Assert.Equal(new Output(EmojiTriggerAction.UpdateQuery("s"), false), sut.Reduce(EmojiTriggerInput.Backspace, 5));
    }

    [Fact]
    public void BackspaceOnEmptyQuery_Cancels()
    {
        var sut = Capturing();

        Assert.Equal(new Output(EmojiTriggerAction.Cancel, false), sut.Reduce(EmojiTriggerInput.Backspace, 0));
        Assert.False(sut.IsCapturing);
    }

    [Fact]
    public void Whitespace_CancelsCapture()
    {
        var sut = Capturing("smi");

        Assert.Equal(new Output(EmojiTriggerAction.Cancel, false), Type(sut, " ", matchCount: 4));
        Assert.False(sut.IsCapturing);
    }

    [Fact]
    public void TerminatorIsRememberedAsTheBoundaryForTheNextColon()
    {
        // Whitespace ends the capture at a boundary, so ":sm :" reopens. Punctuation does not, so
        // the colon of ":sm.:" is treated like any colon inside a token.
        var afterSpace = Capturing("sm ");
        Assert.Equal(EmojiTriggerAction.Open, Type(afterSpace, ":").Action);

        var afterPunctuation = Capturing("sm");
        Assert.Equal(EmojiTriggerAction.Cancel, Type(afterPunctuation, ".").Action);
        Assert.Equal(Output.Ignored, Type(afterPunctuation, ":"));
    }

    [Fact]
    public void UnderscoreAndHyphenExtendQuery() =>
        Assert.Equal("thumbs_up-", Capturing("thumbs_up-").Query);

    // Navigation

    [Fact]
    public void Navigate_WithMatches_ConsumesAndMoves()
    {
        var sut = Capturing("s", matchCount: 3);

        var output = sut.Reduce(EmojiTriggerInput.Navigate(EmojiSelectionMove.Down), 3);

        Assert.Equal(new Output(EmojiTriggerAction.MoveSelection(EmojiSelectionMove.Down), true), output);
        Assert.Equal("s", sut.Query);
    }

    [Fact]
    public void Navigate_WithoutMatches_CancelsAndPassesThrough()
    {
        var sut = Capturing("zz");

        var output = sut.Reduce(EmojiTriggerInput.Navigate(EmojiSelectionMove.Down), 0);

        Assert.Equal(new Output(EmojiTriggerAction.Cancel, false), output);
        Assert.False(sut.IsCapturing);
    }

    // Commit

    [Fact]
    public void Commit_WithMatches_ConsumesAndCommitsByKey()
    {
        var sut = Capturing("smile", matchCount: 4);

        var output = sut.Reduce(EmojiTriggerInput.CommitKey, 4);

        Assert.Equal(new Output(EmojiTriggerAction.Commit(EmojiCommitMode.Key), true), output);
        Assert.False(sut.IsCapturing);
    }

    [Fact]
    public void Commit_WithoutMatches_PassesThrough()
    {
        var sut = Capturing("zz");

        Assert.Equal(new Output(EmojiTriggerAction.Cancel, false), sut.Reduce(EmojiTriggerInput.CommitKey, 0));
    }

    [Fact]
    public void ClosingColon_Commits_AndIsNotConsumed()
    {
        var sut = Capturing("smile", matchCount: 4);

        var output = Type(sut, ":", matchCount: 4);

        Assert.Equal(new Output(EmojiTriggerAction.Commit(EmojiCommitMode.ClosingColon), false), output);
        Assert.False(sut.IsCapturing);
    }

    [Fact]
    public void EmptyQueryClosingColon_Commits()
    {
        var sut = Capturing();

        // The session leaves the literal "::" untouched because an empty query has no match.
        Assert.Equal(new Output(EmojiTriggerAction.Commit(EmojiCommitMode.ClosingColon), false), Type(sut, ":"));
        Assert.False(sut.IsCapturing);
    }

    // Cancellation

    [Fact]
    public void Escape_ConsumesAndCancels()
    {
        var sut = Capturing("s", matchCount: 3);

        Assert.Equal(new Output(EmojiTriggerAction.Cancel, true), sut.Reduce(EmojiTriggerInput.Escape, 3));
        Assert.False(sut.IsCapturing);
    }

    [Fact]
    public void DismissExternally_CancelsWithoutConsuming()
    {
        var sut = Capturing("s", matchCount: 3);

        Assert.Equal(new Output(EmojiTriggerAction.Cancel, false), sut.Reduce(EmojiTriggerInput.DismissExternally, 3));
        Assert.False(sut.IsCapturing);
    }

    [Fact]
    public void NonCharacterInputsWhileIdle_AreIgnoredAndEraseBoundaryKnowledge()
    {
        EmojiTriggerInput[] inputs =
        [
            EmojiTriggerInput.Backspace,
            EmojiTriggerInput.Navigate(EmojiSelectionMove.Down),
            EmojiTriggerInput.CommitKey,
            EmojiTriggerInput.Escape,
            EmojiTriggerInput.DismissExternally,
        ];
        foreach (var input in inputs)
        {
            var sut = new EmojiTriggerStateMachine();
            Type(sut, "a");

            Assert.Equal(Output.Ignored, sut.Reduce(input, 3));
            Assert.False(sut.IsCapturing);
            // The preceding "a" is forgotten, so the next colon is judged like the start of the field.
            Assert.Equal(EmojiTriggerAction.Open, Type(sut, ":").Action);
        }
    }

    [Fact]
    public void Reset_ReturnsToIdleAndForgetsThePrecedingCharacter()
    {
        var sut = Capturing("smi");

        sut.Reset();

        Assert.False(sut.IsCapturing);
        Assert.Equal("", sut.Query);
        Assert.Equal(EmojiTriggerAction.Open, Type(sut, ":").Action);
    }

    // Keyboard events

    [Fact]
    public void KeyboardEvents_DriveTheSameTransitions()
    {
        var sut = new EmojiTriggerStateMachine();

        Assert.Equal(EmojiTriggerAction.Open, sut.Reduce(Key(KeyKind.Character, ':'), 0).Action);
        Assert.Equal(EmojiTriggerAction.UpdateQuery("s"), sut.Reduce(Key(KeyKind.Character, 's'), 0).Action);
        Assert.Equal(EmojiTriggerAction.UpdateQuery(""), sut.Reduce(Key(KeyKind.Backspace), 3).Action);
        Assert.Equal(new Output(EmojiTriggerAction.Commit(EmojiCommitMode.Key), true), sut.Reduce(Key(KeyKind.Tab), 3));
    }

    [Fact]
    public void KeyboardEvents_EscapeIsConsumed() =>
        Assert.Equal(new Output(EmojiTriggerAction.Cancel, true), Capturing("s").Reduce(Key(KeyKind.Escape), 3));

    [Theory]
    [InlineData(KeyKind.Enter)]
    [InlineData(KeyKind.Delete)]
    [InlineData(KeyKind.Arrow)]
    public void KeyboardEvents_EnterDeleteAndArrowsDismissAndPassThrough(KeyKind kind)
    {
        var sut = Capturing("smile", matchCount: 4);

        Assert.Equal(new Output(EmojiTriggerAction.Cancel, false), sut.Reduce(Key(kind), 4));
        Assert.False(sut.IsCapturing);
    }

    [Fact]
    public void KeyboardEvents_ModifiedKeysDismissCapture()
    {
        // Ctrl+Tab style shortcuts must never be read as the commit key.
        var sut = Capturing("smile", matchCount: 4);

        Assert.Equal(new Output(EmojiTriggerAction.Cancel, false), sut.Reduce(Key(KeyKind.Tab, modifier: true), 4));

        // Ctrl+Backspace deletes a whole word, which the single character query cannot track.
        sut = Capturing("smile", matchCount: 4);
        Assert.Equal(EmojiTriggerAction.Cancel, sut.Reduce(Key(KeyKind.Backspace, modifier: true), 4).Action);
    }

    [Fact]
    public void KeyboardEvents_KeyReleasesAndBareModifierKeysAreIgnored()
    {
        var sut = new EmojiTriggerStateMachine();
        Type(sut, "a");

        // Shift goes down before the colon. It must not erase the memory of the preceding "a".
        Assert.Equal(Output.Ignored, sut.Reduce(Key(KeyKind.Other), 0));
        Assert.Equal(Output.Ignored, sut.Reduce(Key(KeyKind.Character, ':'), 0));

        sut = Capturing("smi", matchCount: 2);
        Assert.Equal(Output.Ignored, sut.Reduce(Key(KeyKind.Other), 2));
        Assert.Equal(Output.Ignored, sut.Reduce(Key(KeyKind.Tab, down: false), 2));
        Assert.Equal("smi", sut.Query);
    }
}
