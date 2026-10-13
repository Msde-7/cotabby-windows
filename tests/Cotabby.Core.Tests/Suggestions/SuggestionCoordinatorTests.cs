using Cotabby.Core.Input;
using Cotabby.Core.Suggestions;

namespace Cotabby.Core.Tests.Suggestions;

public sealed class SuggestionCoordinatorTests : CoordinatorHarness
{
    [Fact]
    public async Task Typing_ShowsASuggestionForTheTextIncludingTheLastKey()
    {
        Type("hello");

        await Shown(" world again");
        Assert.Equal("hello", Engine.Requests.Last().Prefix);
    }

    [Fact]
    public async Task Typing_WaitsForTheHostToPublishTheKey()
    {
        Type("hell");
        await Shown(" world again");
        Engine.Requests.Clear();

        Hook.Press(Keys.Char('o'));
        await Task.Delay(80);
        Focus.Append("o");

        await Eventually(() => !Engine.Requests.IsEmpty, "a request is sent");
        Assert.Equal("hello", Engine.Requests.First().Prefix);
    }

    [Fact]
    public async Task AcceptWord_SwallowsTheKeyAndInsertsOneWord()
    {
        Type("hello");
        await Shown(" world again");

        Assert.True(Hook.Press(Keys.Tab()));

        await Shown(" again");
        await Eventually(() => Focus.Current!.Text == "hello world", "the word is inserted");
    }

    [Fact]
    public void AcceptKey_PassesThroughWithoutASuggestion()
    {
        Assert.False(Hook.Press(Keys.Tab()));
    }

    [Fact]
    public async Task AcceptAll_InsertsTheWholeSuggestion()
    {
        Type("hello");
        await Shown(" world again");

        Assert.True(Hook.Press(Backtick));

        await Eventually(() => Focus.Insertions.Contains(" world again"), "everything is inserted");
    }

    [Fact]
    public async Task TypingTheSuggestion_AdvancesItWithoutANewRequest()
    {
        Type("hello");
        await Shown(" world again");
        int requests = Engine.Requests.Count;

        Type(" w");

        await Shown("orld again");
        Assert.Equal(requests, Engine.Requests.Count);
    }

    [Fact]
    public async Task TypingSomethingElse_ReplacesTheSuggestion()
    {
        Type("hello");
        await Shown(" world again");
        Engine.Completion = _ => " there";

        Type("x");

        await Shown(" there");
    }

    [Fact]
    public async Task Escape_HidesTheSuggestion()
    {
        Type("hello");
        await Shown(" world again");

        Hook.Press(Keys.Special(KeyKind.Escape, Keys.VkEscape));

        await Shown("");
    }

    [Fact]
    public async Task DismissedSuggestion_IsNotOfferedAgainForTheSameText()
    {
        Type("hello");
        await Shown(" world again");
        Hook.Press(Keys.Special(KeyKind.Escape, Keys.VkEscape));
        await Shown("");
        Engine.Requests.Clear();

        Type(" ");

        await Eventually(() => !Engine.Requests.IsEmpty, "the engine is asked again");
        await StaysHidden();
    }

    [Fact]
    public async Task TypingThroughASuggestion_MovesTheGhostToTheCaret()
    {
        Type("hello");
        await Shown(" world again");
        var moved = Focus.Current!.CaretRect with { X = 99 };

        Hook.Press(Keys.Char(' '));
        Focus.Append(" ");
        Focus.Move(moved);

        await Eventually(() => Overlay.Anchor == moved, "the overlay follows the caret");
        Assert.Equal("world again", Overlay.Text);
    }

    [Fact]
    public async Task DoubleTap_TakesTheRestOnTheSecondPress()
    {
        Coordinator.Settings = SuggestionSettings.Default with { DoubleTapAcceptsAll = true };
        Engine.Completion = _ => " one two three";
        Type("hello");
        await Shown(" one two three");

        Hook.Press(Keys.Tab());
        Hook.Press(Keys.Tab());

        await Eventually(() => Focus.Insertions.SequenceEqual([" one", " two three"]), "the pair takes everything");
    }

    [Fact]
    public async Task DoubleTap_IgnoresAHeldKey()
    {
        Coordinator.Settings = SuggestionSettings.Default with { DoubleTapAcceptsAll = true };
        Engine.Completion = _ => " one two three";
        Type("hello");
        await Shown(" one two three");

        Hook.Press(Keys.Tab());
        Hook.Press(Keys.Tab(repeat: true));

        await Eventually(() => Focus.Insertions.SequenceEqual([" one", " two"]), "the held key accepts word by word");
    }

    [Fact]
    public async Task AddSpaceAfterAccept_TakesTheFollowingSpaceWithEachWord()
    {
        Coordinator.Settings = SuggestionSettings.Default with { AddSpaceAfterAccept = true };
        Engine.Completion = r => r.Prefix.EndsWith("hello") ? " one two" : "";
        Type("hello");
        await Shown(" one two");

        Hook.Press(Keys.Tab());
        await Shown("two");
        Hook.Press(Keys.Tab());

        await Eventually(() => Focus.Insertions.SequenceEqual([" one ", "two "]), "each accept ends with a space");
    }

    [Fact]
    public async Task PhraseMode_AcceptsUpToTheSentenceEnd()
    {
        Coordinator.Settings = SuggestionSettings.Default with { Granularity = AcceptanceGranularity.Phrase };
        Engine.Completion = _ => " is done. Next one";
        Type("hello");
        await Shown(" is done. Next one");

        Hook.Press(Keys.Tab());

        await Eventually(() => Focus.Insertions.SequenceEqual([" is done."]), "the phrase is inserted");
    }

    [Fact]
    public async Task AcceptKeyPressedWhileTheContinuationLoads_IsQueuedForIt()
    {
        Engine.Completion = r => r.Prefix.EndsWith("hello") ? " world" : " again soon";
        Type("hello");
        await Shown(" world");

        Assert.True(Hook.Press(Keys.Tab()));
        Assert.True(Hook.Press(Keys.Tab()), "the key stays owned while the continuation generates");

        await Eventually(() => Focus.Insertions.SequenceEqual([" world", " again"]), "the queued accept lands");
    }

    [Fact]
    public async Task PerAppOverride_ReplacesTheAcceptKey()
    {
        var f6 = new KeyBinding(0x75, KeyModifiers.None, "F6");
        Coordinator.Settings = SuggestionSettings.Default with
        {
            ShortcutOverrides = [new PerAppShortcutOverride { ProcessName = "notepad", AcceptWord = f6 }],
        };
        Type("hello");
        await Shown(" world again");

        Assert.True(Hook.Press(Keys.Special(KeyKind.Other, 0x75)));
        await Shown(" again");
        Assert.False(Hook.Press(Keys.Tab()));
    }

    [Fact]
    public async Task FocusMovingToAnotherFieldOfTheSameApp_HidesTheSuggestion()
    {
        Type("hello");
        await Shown(" world again");

        Focus.Focus(Fields.Text("Search") with { FieldId = "search" });

        await Shown("");
        Assert.False(Hook.Press(Keys.Tab()));
    }

    [Fact]
    public async Task CaretMovedByTheMouse_HidesTheSuggestion()
    {
        Type("hello there friend");
        await Shown(" world again");

        Focus.Replace(Focus.Current! with { CaretOffset = 2 });

        await Shown("");
    }

    [Fact]
    public async Task AcceptAfterTheCaretMoved_InsertsNothing()
    {
        Type("hello");
        await Shown(" world again");

        Focus.Replace(Fields.Text("Search"));
        Hook.Press(Keys.Tab());

        await Shown("");
        Assert.Empty(Focus.Insertions);
        Assert.Equal("Search", Focus.Current!.Text);
    }

    [Fact]
    public async Task Shortcut_InvalidatesTheSuggestion()
    {
        Type("hello");
        await Shown(" world again");
        Engine.Completion = _ => "";

        Hook.Press(Keys.Special(KeyKind.Shortcut, 'V') with { Modifiers = KeyModifiers.Control });

        await Shown("");
    }

    [Fact]
    public async Task FocusMovingToAnotherApp_HidesTheSuggestion()
    {
        Type("hello");
        await Shown(" world again");

        Focus.Focus(Fields.Text("other", process: "word", pid: 200));

        await Shown("");
    }

    [Fact]
    public async Task InlineCapture_HidesTheSuggestionAndBlocksNewOnes()
    {
        Type("hello");
        await Shown(" world again");

        Coordinator.InlineCaptureActive = () => true;
        Type(":");

        await Shown("");
        Assert.False(Hook.Press(Keys.Tab()));
    }

    [Fact]
    public async Task Metrics_CountAcceptedWords()
    {
        Type("hello");
        await Shown(" world again");

        Hook.Press(Backtick);

        await Eventually(() => Coordinator.Metrics.WordsAccepted == 2, "both words are counted");
        Assert.Equal(1, Coordinator.Metrics.SuggestionsShown);
        Assert.Equal(1, Coordinator.Metrics.SuggestionsAccepted);
    }

    [Fact]
    public async Task Disabled_NeverAsksTheEngine()
    {
        Coordinator.Settings = SuggestionSettings.Default with { Enabled = false };

        Type("hello");

        await StaysHidden();
        Assert.Empty(Engine.Requests);
    }

    [Fact]
    public async Task Paused_NeverAsksTheEngine()
    {
        Coordinator.Settings = SuggestionSettings.Default with { Pause = new PauseState(Until: null) };

        Type("hello");

        await StaysHidden();
        Assert.Empty(Engine.Requests);
    }

    [Fact]
    public async Task BatterySaver_PausesSuggestionsUnlessOptedOut()
    {
        BatterySaver = true;
        Type("hello");
        await StaysHidden();

        Coordinator.Settings = SuggestionSettings.Default with { PauseOnBatterySaver = false };
        Type("x");
        await Shown(" world again");
    }

    [Theory]
    [InlineData("WindowsTerminal", false)]
    [InlineData("slack", true)]
    public async Task FieldRules_BlockTerminalsAndListedApps(string process, bool blockedByList)
    {
        if (blockedByList)
        {
            Coordinator.Settings = SuggestionSettings.Default with
            {
                BlockedApps = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Slack" },
            };
        }
        Focus.Focus(Fields.Text("", process));

        Type("hello");

        await StaysHidden();
        Assert.Empty(Engine.Requests);
    }

    [Fact]
    public async Task SignInField_IsLeftAlone()
    {
        Focus.Focus(Fields.Text("", singleLine: true) with { Label = "Email or phone" });

        Type("hello");

        await StaysHidden();
        Assert.Empty(Engine.Requests);
    }
}
