using Cotabby.Core.Input;

namespace Cotabby.Core.Tests.Input;

public sealed class ImeCompositionTrackerTests
{
    private const int Japanese = 0x0411;
    private const int Korean = 0x0412;
    private const int SimplifiedChinese = 0x0804;

    private static KeyboardEvent Char(char c) => new() { Kind = KeyKind.Character, Character = c, IsKeyDown = true };
    private static KeyboardEvent Key(KeyKind kind) => new() { Kind = kind, Character = '\0', IsKeyDown = true };

    private static ImeCompositionTracker Converting(int language)
    {
        var tracker = new ImeCompositionTracker();
        tracker.Observe(language);
        return tracker;
    }

    [Fact]
    public void NothingComposesWithoutAConvertingInputMethod()
    {
        var tracker = new ImeCompositionTracker();
        tracker.Key(Char('k'));
        Assert.False(tracker.IsComposing);
    }

    [Fact]
    public void JapaneseComposesThroughSpaceUntilEnterCommits()
    {
        var tracker = Converting(Japanese);
        tracker.Key(Char('k'));
        tracker.Key(Char('a'));
        Assert.True(tracker.IsComposing);
        Assert.False(tracker.Key(Char(' ')));
        Assert.True(tracker.IsComposing);
        Assert.True(tracker.Key(Key(KeyKind.Enter)));
        Assert.False(tracker.IsComposing);
    }

    [Fact]
    public void EscapeAbandonsTheCompositionWithoutCommitting()
    {
        var tracker = Converting(Japanese);
        tracker.Key(Char('k'));
        Assert.False(tracker.Key(Key(KeyKind.Escape)));
        Assert.False(tracker.IsComposing);
    }

    [Theory]
    [InlineData(' ')]
    [InlineData('.')]
    public void KoreanCommitsAtASpaceOrPunctuation(char commit)
    {
        var tracker = Converting(Korean);
        tracker.Key(Char('g'));
        Assert.True(tracker.IsComposing);
        Assert.True(tracker.Key(Char(commit)));
        Assert.False(tracker.IsComposing);
    }

    [Theory]
    [InlineData(' ')]
    [InlineData('2')]
    public void ChineseCommitsWithSpaceOrACandidateNumber(char commit)
    {
        var tracker = Converting(SimplifiedChinese);
        tracker.Key(Char('n'));
        tracker.Key(Char('i'));
        Assert.True(tracker.Key(Char(commit)));
        Assert.False(tracker.IsComposing);
    }

    [Fact]
    public void SpaceOnAnIdleInputMethodDoesNotStartComposing()
    {
        var tracker = Converting(Japanese);
        tracker.Key(Char(' '));
        Assert.False(tracker.IsComposing);
    }

    [Fact]
    public void ShortcutsAndNavigationLeaveTheCompositionAlone()
    {
        var tracker = Converting(Japanese);
        tracker.Key(Char('k'));
        tracker.Key(Key(KeyKind.Arrow));
        tracker.Key(Key(KeyKind.Backspace));
        tracker.Key(new KeyboardEvent { Kind = KeyKind.Character, Character = 'c', IsKeyDown = true, Modifiers = KeyModifiers.Control });
        Assert.True(tracker.IsComposing);
    }

    [Fact]
    public void TheInputMethodLeavingNativeModeEndsTheComposition()
    {
        var tracker = Converting(Japanese);
        tracker.Key(Char('k'));
        tracker.Observe(0);
        Assert.False(tracker.IsComposing);
    }
}
