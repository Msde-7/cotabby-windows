using System.Diagnostics;
using Cotabby.Core.Input;
using Cotabby.Core.Suggestions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cotabby.Core.Tests.Suggestions;

public sealed class SuggestionCoordinatorTests : IAsyncDisposable
{
    private static readonly KeyboardEvent Backtick = Keys.Special(KeyKind.Character, KeyBinding.VkBacktick) with { Character = '`' };

    private readonly FakeHook _hook = new();
    private readonly FakeFocus _focus = new();
    private readonly ScriptedEngine _engine = new();
    private readonly RecordingOverlay _overlay = new();
    private readonly PumpContext _pump = new();
    private readonly SuggestionCoordinator _coordinator;
    private bool _batterySaver;

    public SuggestionCoordinatorTests()
    {
        _coordinator = new SuggestionCoordinator(
            _hook, _focus, _engine, _overlay, _focus,
            new SuggestionWorkController(TimeSpan.FromMilliseconds(5)),
            NullLogger<SuggestionCoordinator>.Instance, _pump,
            batterySaverOn: () => _batterySaver);
        _coordinator.Start();
        _focus.Focus(Fields.Text(""));
    }

    public async ValueTask DisposeAsync()
    {
        await _coordinator.DisposeAsync();
        _pump.Dispose();
    }

    private void Type(string text)
    {
        foreach (char c in text)
        {
            // The hook sees the key before the host applies it.
            _hook.Press(Keys.Char(c));
            _focus.Append(c.ToString());
        }
    }

    private static async Task Eventually(Func<bool> condition, string because)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(clock.ElapsedMilliseconds < 5000, "Timed out waiting until " + because);
            await Task.Delay(5);
        }
    }

    private Task Shown(string text) => Eventually(() => _overlay.Text == text, $"the overlay shows \"{text}\"");

    private async Task StaysHidden()
    {
        await Task.Delay(150);
        Assert.Equal("", _overlay.Text);
    }

    [Fact]
    public async Task Typing_ShowsASuggestionForTheTextIncludingTheLastKey()
    {
        Type("hello");

        await Shown(" world again");
        Assert.Equal("hello", _engine.Requests.Last().Prefix);
    }

    [Fact]
    public async Task Typing_WaitsForTheHostToPublishTheKey()
    {
        Type("hell");
        await Shown(" world again");
        _engine.Requests.Clear();

        _hook.Press(Keys.Char('o'));
        await Task.Delay(80);
        _focus.Append("o");

        await Eventually(() => !_engine.Requests.IsEmpty, "a request is sent");
        Assert.Equal("hello", _engine.Requests.First().Prefix);
    }

    [Fact]
    public async Task AcceptWord_SwallowsTheKeyAndInsertsOneWord()
    {
        Type("hello");
        await Shown(" world again");

        Assert.True(_hook.Press(Keys.Tab()));

        await Shown(" again");
        await Eventually(() => _focus.Current!.Text == "hello world", "the word is inserted");
    }

    [Fact]
    public void AcceptKey_PassesThroughWithoutASuggestion()
    {
        Assert.False(_hook.Press(Keys.Tab()));
    }

    [Fact]
    public async Task AcceptAll_InsertsTheWholeSuggestion()
    {
        Type("hello");
        await Shown(" world again");

        Assert.True(_hook.Press(Backtick));

        await Eventually(() => _focus.Insertions.Contains(" world again"), "everything is inserted");
    }

    [Fact]
    public async Task TypingTheSuggestion_AdvancesItWithoutANewRequest()
    {
        Type("hello");
        await Shown(" world again");
        int requests = _engine.Requests.Count;

        Type(" w");

        await Shown("orld again");
        Assert.Equal(requests, _engine.Requests.Count);
    }

    [Fact]
    public async Task TypingSomethingElse_ReplacesTheSuggestion()
    {
        Type("hello");
        await Shown(" world again");
        _engine.Completion = _ => " there";

        Type("x");

        await Shown(" there");
    }

    [Fact]
    public async Task Escape_HidesTheSuggestion()
    {
        Type("hello");
        await Shown(" world again");

        _hook.Press(Keys.Special(KeyKind.Escape, Keys.VkEscape));

        await Shown("");
    }

    [Fact]
    public async Task DoubleTap_TakesTheRestOnTheSecondPress()
    {
        _coordinator.Settings = SuggestionSettings.Default with { DoubleTapAcceptsAll = true };
        _engine.Completion = _ => " one two three";
        Type("hello");
        await Shown(" one two three");

        _hook.Press(Keys.Tab());
        _hook.Press(Keys.Tab());

        await Eventually(() => _focus.Insertions.SequenceEqual([" one", " two three"]), "the pair takes everything");
    }

    [Fact]
    public async Task DoubleTap_IgnoresAHeldKey()
    {
        _coordinator.Settings = SuggestionSettings.Default with { DoubleTapAcceptsAll = true };
        _engine.Completion = _ => " one two three";
        Type("hello");
        await Shown(" one two three");

        _hook.Press(Keys.Tab());
        _hook.Press(Keys.Tab(repeat: true));

        await Eventually(() => _focus.Insertions.SequenceEqual([" one", " two"]), "the held key accepts word by word");
    }

    [Fact]
    public async Task AddSpaceAfterAccept_TakesTheFollowingSpaceWithEachWord()
    {
        _coordinator.Settings = SuggestionSettings.Default with { AddSpaceAfterAccept = true };
        _engine.Completion = r => r.Prefix.EndsWith("hello") ? " one two" : "";
        Type("hello");
        await Shown(" one two");

        _hook.Press(Keys.Tab());
        await Shown("two");
        _hook.Press(Keys.Tab());

        await Eventually(() => _focus.Insertions.SequenceEqual([" one ", "two "]), "each accept ends with a space");
    }

    [Fact]
    public async Task PhraseMode_AcceptsUpToTheSentenceEnd()
    {
        _coordinator.Settings = SuggestionSettings.Default with { Granularity = AcceptanceGranularity.Phrase };
        _engine.Completion = _ => " is done. Next one";
        Type("hello");
        await Shown(" is done. Next one");

        _hook.Press(Keys.Tab());

        await Eventually(() => _focus.Insertions.SequenceEqual([" is done."]), "the phrase is inserted");
    }

    [Fact]
    public async Task AcceptKeyPressedWhileTheContinuationLoads_IsQueuedForIt()
    {
        _engine.Completion = r => r.Prefix.EndsWith("hello") ? " world" : " again soon";
        Type("hello");
        await Shown(" world");

        Assert.True(_hook.Press(Keys.Tab()));
        Assert.True(_hook.Press(Keys.Tab()), "the key stays owned while the continuation generates");

        await Eventually(() => _focus.Insertions.SequenceEqual([" world", " again"]), "the queued accept lands");
    }

    [Fact]
    public async Task PerAppOverride_ReplacesTheAcceptKey()
    {
        var f6 = new KeyBinding(0x75, KeyModifiers.None, "F6");
        _coordinator.Settings = SuggestionSettings.Default with
        {
            ShortcutOverrides = [new PerAppShortcutOverride { ProcessName = "notepad", AcceptWord = f6 }],
        };
        Type("hello");
        await Shown(" world again");

        Assert.False(_hook.Press(Keys.Tab()));
        Type("hello");
        await Shown(" world again");
        Assert.True(_hook.Press(Keys.Special(KeyKind.Other, 0x75)));
    }

    [Fact]
    public async Task FocusMovingToAnotherApp_HidesTheSuggestion()
    {
        Type("hello");
        await Shown(" world again");

        _focus.Focus(Fields.Text("other", process: "word", pid: 200));

        await Shown("");
    }

    [Fact]
    public async Task InlineCapture_HidesTheSuggestionAndBlocksNewOnes()
    {
        Type("hello");
        await Shown(" world again");

        _coordinator.InlineCaptureActive = () => true;
        Type(":");

        await Shown("");
        Assert.False(_hook.Press(Keys.Tab()));
    }

    [Fact]
    public async Task Metrics_CountAcceptedWords()
    {
        Type("hello");
        await Shown(" world again");

        _hook.Press(Backtick);

        await Eventually(() => _coordinator.Metrics.WordsAccepted == 2, "both words are counted");
        Assert.Equal(1, _coordinator.Metrics.SuggestionsShown);
        Assert.Equal(1, _coordinator.Metrics.SuggestionsAccepted);
    }

    [Fact]
    public async Task Disabled_NeverAsksTheEngine()
    {
        _coordinator.Settings = SuggestionSettings.Default with { Enabled = false };

        Type("hello");

        await StaysHidden();
        Assert.Empty(_engine.Requests);
    }

    [Fact]
    public async Task Paused_NeverAsksTheEngine()
    {
        _coordinator.Settings = SuggestionSettings.Default with { Pause = new PauseState(Until: null) };

        Type("hello");

        await StaysHidden();
        Assert.Empty(_engine.Requests);
    }

    [Fact]
    public async Task BatterySaver_PausesSuggestionsUnlessOptedOut()
    {
        _batterySaver = true;
        Type("hello");
        await StaysHidden();

        _coordinator.Settings = SuggestionSettings.Default with { PauseOnBatterySaver = false };
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
            _coordinator.Settings = SuggestionSettings.Default with
            {
                BlockedApps = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Slack" },
            };
        }
        _focus.Focus(Fields.Text("", process));

        Type("hello");

        await StaysHidden();
        Assert.Empty(_engine.Requests);
    }

    [Fact]
    public async Task SignInField_IsLeftAlone()
    {
        _focus.Focus(Fields.Text("", singleLine: true) with { Label = "Email or phone" });

        Type("hello");

        await StaysHidden();
        Assert.Empty(_engine.Requests);
    }
}
