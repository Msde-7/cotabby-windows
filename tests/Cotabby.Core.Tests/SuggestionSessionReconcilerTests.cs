using Cotabby.Core.Focus;
using Cotabby.Core.Input;
using Cotabby.Core.Suggestions;
using Xunit;

namespace Cotabby.Core.Tests;

public class SuggestionSessionReconcilerTests
{
    private static SuggestionSession MakeSession(string visible) => new()
    {
        RequestId = "req_test",
        ElementHandle = new object(),
        OriginalPrefix = "hello ",
        ConsumedChars = 0,
        VisibleText = visible,
        IsComplete = true,
        AnchorRect = new ScreenRect(0, 0, 1, 1),
    };

    private static KeyboardEvent Key(KeyKind kind, char ch = '\0', bool down = true, bool mod = false) =>
        new() { Kind = kind, Character = ch, IsKeyDown = down, HasNonShiftModifier = mod };

    [Fact]
    public void NullSession_ReturnsIgnore()
    {
        var r = SuggestionSessionReconciler.Apply(null, Key(KeyKind.Tab));
        Assert.Equal(SuggestionSessionReconciler.Outcome.Ignore, r.Outcome);
        Assert.Null(r.Next);
    }

    [Fact]
    public void Tab_OnVisibleSession_ReturnsAccept()
    {
        var s = MakeSession("world");
        var r = SuggestionSessionReconciler.Apply(s, Key(KeyKind.Tab));
        Assert.Equal(SuggestionSessionReconciler.Outcome.Accept, r.Outcome);
    }

    [Fact]
    public void Tab_OnEmptyVisibleSession_IsIgnored()
    {
        var s = MakeSession("");
        var r = SuggestionSessionReconciler.Apply(s, Key(KeyKind.Tab));
        Assert.Equal(SuggestionSessionReconciler.Outcome.Ignore, r.Outcome);
    }

    [Fact]
    public void MatchingChar_AdvancesVisible()
    {
        var s = MakeSession("world");
        var r = SuggestionSessionReconciler.Apply(s, Key(KeyKind.Character, 'w'));
        Assert.Equal(SuggestionSessionReconciler.Outcome.AdvanceVisible, r.Outcome);
        Assert.Equal("orld", r.Next!.VisibleText);
        Assert.Equal(1, r.Next.ConsumedChars);
    }

    [Fact]
    public void NonMatchingChar_Cancels()
    {
        var s = MakeSession("world");
        var r = SuggestionSessionReconciler.Apply(s, Key(KeyKind.Character, 'x'));
        Assert.Equal(SuggestionSessionReconciler.Outcome.Cancel, r.Outcome);
        Assert.Null(r.Next);
    }

    [Theory]
    [InlineData(KeyKind.Escape)]
    [InlineData(KeyKind.Enter)]
    [InlineData(KeyKind.Arrow)]
    [InlineData(KeyKind.Delete)]
    [InlineData(KeyKind.Backspace)]
    public void NavigationKeys_Cancel(KeyKind kind)
    {
        var s = MakeSession("world");
        var r = SuggestionSessionReconciler.Apply(s, Key(kind));
        Assert.Equal(SuggestionSessionReconciler.Outcome.Cancel, r.Outcome);
        Assert.Null(r.Next);
    }

    [Fact]
    public void KeyUp_IsAlwaysIgnored()
    {
        var s = MakeSession("world");
        var r = SuggestionSessionReconciler.Apply(s, Key(KeyKind.Character, 'w', down: false));
        Assert.Equal(SuggestionSessionReconciler.Outcome.Ignore, r.Outcome);
        Assert.Same(s, r.Next);
    }

    [Fact]
    public void Consume_AllChars_LeavesEmptyVisible()
    {
        var s = MakeSession("hi");
        var step1 = SuggestionSessionReconciler.Apply(s, Key(KeyKind.Character, 'h'));
        var step2 = SuggestionSessionReconciler.Apply(step1.Next, Key(KeyKind.Character, 'i'));
        Assert.Equal(SuggestionSessionReconciler.Outcome.AdvanceVisible, step2.Outcome);
        Assert.Equal("", step2.Next!.VisibleText);
        Assert.Equal(2, step2.Next.ConsumedChars);
    }
}
