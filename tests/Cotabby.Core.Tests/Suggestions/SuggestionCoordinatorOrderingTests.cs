using Cotabby.Core.Input;
using Cotabby.Core.Suggestions;

namespace Cotabby.Core.Tests.Suggestions;

/// <summary>Keys and edits that race an accept, and the host-publish wait around them.</summary>
public sealed class SuggestionCoordinatorOrderingTests : CoordinatorHarness
{
    [Fact]
    public async Task KeyTypedWhileAnAcceptIsInserting_LandsAfterTheAcceptedText()
    {
        Engine.Completion = r => r.Prefix.EndsWith("hel") ? "lo world" : "";
        Type("hel");
        await Shown("lo world");
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Focus.InsertGate = gate;

        Assert.True(Hook.Press(Keys.Tab()));
        // The host has not received the accepted text yet, so the space is held back for it.
        Assert.True(Hook.Press(Keys.Char(' ')), "a key typed during the insertion is held");
        Assert.True(Hook.Press(Keys.Char('a')));
        await KeysHandled();
        gate.SetResult();

        await Eventually(() => Focus.Current!.Text == "hello a", "the held keys follow the accepted text");
        await HoldEnded();
        Assert.False(Hook.Press(Keys.Char('b')), "typing passes through once the insertion is done");
    }

    [Fact]
    public async Task ABackspaceTypedWhileAnAcceptIsInserting_DeletesTheKeyTypedBeforeIt()
    {
        Engine.Completion = r => r.Prefix.EndsWith("hel") ? "lo world" : "";
        Type("hel");
        await Shown("lo world");
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Focus.InsertGate = gate;
        int handledX = 0;
        Hook.KeyEvent += (_, args) => handledX += args.Event.Character == 'x' ? 1 : 0;

        Assert.True(Hook.Press(Keys.Tab()));
        Assert.True(Hook.Hit(Keys.Char('x')));
        Assert.True(Hook.Hit(Keys.Special(KeyKind.Backspace, 0x08)), "a Backspace is held like a character");
        Assert.Equal(0, handledX);
        gate.SetResult();

        await HoldEnded();
        await Settled();
        Assert.Equal("hello", Focus.Current!.Text);
        Assert.Equal(1, handledX);
        Assert.Equal(["lo"], Focus.Insertions);
    }

    [Fact]
    public async Task AnEnterTypedWhileAnAcceptIsInserting_BreaksTheLineAfterTheAcceptedText()
    {
        Engine.Completion = r => r.Prefix.EndsWith("hel") ? "lo world" : "";
        Type("hel");
        await Shown("lo world");
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Focus.InsertGate = gate;

        Assert.True(Hook.Press(Keys.Tab()));
        Assert.True(Hook.Hit(Keys.Special(KeyKind.Enter, 0x0D)));
        gate.SetResult();

        await HoldEnded();
        Assert.Equal("hello\n", Focus.Current!.Text);
    }

    [Fact]
    public async Task ADeadKeyAndItsLetterTypedWhileAnAcceptIsInserting_FollowTheAcceptedText()
    {
        Engine.Completion = r => r.Prefix.EndsWith("hel") ? "lo world" : "";
        Type("hel");
        await Shown("lo world");
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Focus.InsertGate = gate;

        Assert.True(Hook.Press(Keys.Tab()));
        // The dead key makes no character. The hook reports U+FFFD for the letter after it, which
        // the host shows accented; here the host shows the U+FFFD itself.
        Assert.True(Hook.Hit(Keys.Special(KeyKind.Other, 0xDE)), "the dead key is held");
        Assert.True(Hook.Hit(Keys.Char('\uFFFD')), "the letter is held");
        gate.SetResult();

        await HoldEnded();
        Assert.Equal("hello\uFFFD", Focus.Current!.Text);
        Assert.Equal(["lo"], Focus.Insertions);
        // Both reach the host as typed, the dead key first, so the host composes them itself.
        var lastTwo = Hook.HostKeys.TakeLast(2).ToList();
        Assert.Equal(0xDE, lastTwo[0].VirtualKey);
        Assert.Equal('\uFFFD', lastTwo[1].Character);
        Assert.All(lastTwo, key => Assert.True(key.IsReplay));
    }

    [Fact]
    public async Task KeysTypedDuringASlowAccept_StayInOrderPastASecond()
    {
        Engine.Completion = r => r.Prefix.EndsWith("hel") ? "lo world" : "";
        Type("hel");
        await Shown("lo world");
        // A paste-accept keeps the host busy for half a second and more.
        Focus.InsertDelay = TimeSpan.FromMilliseconds(600);

        Assert.True(Hook.Press(Keys.Tab()));
        Assert.True(Hook.Hit(Keys.Char('a')));
        await Task.Delay(200);
        Assert.True(Hook.Hit(Keys.Char('b')));
        await Task.Delay(900);
        Hook.Hit(Keys.Char('c'));

        await HoldEnded();
        await Settled();
        Assert.Equal("helloabc", Focus.Current!.Text);
    }

    [Fact]
    public async Task AnAcceptKeyTypedWhileAnAcceptIsInserting_AcceptsTheNextWordAheadOfTheKeysAfterIt()
    {
        Engine.Completion = r => r.Prefix == "hello" ? " world again" : "";
        Type("hello");
        await Shown(" world again");
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Focus.InsertGate = gate;

        Assert.True(Hook.Press(Keys.Tab()));
        Assert.True(Hook.Press(Keys.Tab()), "the second press waits for the first word");
        Assert.True(Hook.Hit(Keys.Char('x')));
        gate.SetResult();

        await HoldEnded();
        await Settled();
        Assert.Equal("hello world againx", Focus.Current!.Text);
    }

    [Fact]
    public async Task AnAcceptWithNothingToInsert_GivesTheTabBackAheadOfTheKeysTypedAfterIt()
    {
        Type("hello");
        await Shown(" world again");

        Focus.Replace(Fields.Text("Search"));
        Assert.True(Hook.Press(Keys.Tab()));
        Hook.Hit(Keys.Char('x'));

        await HoldEnded();
        await Settled();
        // Upstream passes the key through when the suggestion no longer matches the field.
        Assert.Equal("Search\tx", Focus.Current!.Text);
        Assert.Empty(Focus.Insertions);
    }

    [Fact]
    public async Task HeldKeys_AreReleasedWhenTheInsertionTakesTooLong()
    {
        Engine.Completion = r => r.Prefix.EndsWith("hel") ? "lo world" : "";
        Type("hel");
        await Shown("lo world");
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Focus.InsertGate = gate;

        Assert.True(Hook.Press(Keys.Tab()));
        Assert.True(Hook.Press(Keys.Char(' ')));
        await Task.Delay(1200);

        Assert.False(Hook.Press(Keys.Char('x')), "a stuck insertion stops holding keys");
        gate.SetResult();
    }

    [Fact]
    public async Task KeyAfterAPartialAccept_WaitsForTheHostToShowIt()
    {
        Engine.Completion = r => r.Prefix == "hello" ? " world again" : " more";
        Type("hello");
        await Shown(" world again");
        Hook.Press(Keys.Tab());
        await Eventually(() => Focus.Current!.Text == "hello world", "the word is inserted");
        await Settled();
        Engine.Requests.Clear();

        // The field already holds more than the baseline read before the accept, so only counting
        // the accepted word tells this read apart from the one with the key in it.
        Hook.Press(Keys.Char('x'));
        await Task.Delay(80);
        Focus.Append("x");

        await Eventually(() => !Engine.Requests.IsEmpty, "a request is sent");
        Assert.Equal("hello worldx", Engine.Requests.First().Prefix);
    }

    [Fact]
    public async Task KeyTypedJustAfterTheLastChunk_KeepsItsOwnRequest()
    {
        Engine.Completion = r => r.Prefix == "hello" ? " world" : r.Prefix == "hello worldx" ? " marks" : "";
        Type("hello");
        await Shown(" world");
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Focus.InsertGate = gate;

        Hook.Press(Keys.Tab());
        // Held behind the insertion and replayed after it, as a fast typist's next key would be.
        Hook.Press(Keys.Char('x'));
        gate.SetResult();

        await Shown(" marks");
    }

    [Fact]
    public async Task Paste_WaitsForTheHostToShowThePastedText()
    {
        Type("hello");
        await Shown(" world again");
        Engine.Requests.Clear();

        Hook.Press(Keys.Special(KeyKind.Shortcut, 0x56) with { Modifiers = KeyModifiers.Control });
        await Task.Delay(80);
        Focus.Append(" pasted");

        await Eventually(() => !Engine.Requests.IsEmpty, "a request is sent");
        Assert.Equal("hello pasted", Engine.Requests.First().Prefix);
    }

    [Fact]
    public async Task AFailedWord_StopsTheWordsAcceptedAfterIt()
    {
        Engine.Completion = r => r.Prefix == "hello" ? " one two three" : "";
        Type("hello");
        await Shown(" one two three");
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Focus.InsertGate = gate;
        Focus.FailingInserts = 1;

        Assert.True(Hook.Press(Keys.Tab()));
        Assert.True(Hook.Press(Keys.Tab()));
        // The second press is held behind the first word until the hold gives up after a second.
        await Shown(" three");
        gate.SetResult();

        await Shown("");
        await Settled();
        // Neither word lands. Like upstream, the press whose word was dropped goes to the host;
        // the failed one may have typed part of its word, so it keeps its key.
        Assert.Equal("hello\t", Focus.Current!.Text);
    }

    [Fact]
    public async Task AcceptedLineBreak_ReadBackAsCrLf_KeepsTheSuggestion()
    {
        Focus.StoresCrLf = true;
        Engine.Completion = r => r.Prefix == "hello" ? "\nworld again" : "";
        Type("hello");
        await Shown("\nworld again");

        Hook.Press(Keys.Tab());
        await Eventually(() => Focus.Current!.Text == "hello\r\nworld", "the line is inserted");
        await Task.Delay(400);

        Assert.Equal(" again", Overlay.Text);
        Assert.True(Hook.Press(Keys.Tab()), "the next accept still belongs to the suggestion");
        await Eventually(() => Focus.Current!.Text == "hello\r\nworld again", "the next word is inserted");
    }

    [Fact]
    public async Task BackspacingATypo_BringsTheSuggestionBackWithoutGenerating()
    {
        Engine.Completion = r => r.Prefix == "hello" ? " world again" : "";
        Type("hello");
        await Shown(" world again");
        Type("x");
        await Eventually(() => Engine.Requests.Any(r => r.Prefix == "hellox"), "the typo is asked about");
        await Settled();
        Engine.Requests.Clear();

        Hook.Press(Keys.Special(KeyKind.Backspace, 0x08));
        Focus.Replace(Fields.Text("hello"));

        await Shown(" world again");
        Assert.Empty(Engine.Requests);
    }

    [Fact]
    public async Task ABroughtBackSuggestion_IsDrawnInTheHostsFont()
    {
        Focus.HostFontName = "Segoe UI";
        Engine.Completion = r => r.Prefix == "hello" ? " world again" : "";
        Type("hello");
        await Shown(" world again");
        Type("x");
        await Eventually(() => Engine.Requests.Any(r => r.Prefix == "hellox"), "the typo is asked about");
        await Settled();

        Hook.Press(Keys.Special(KeyKind.Backspace, 0x08));
        Focus.Replace(Fields.Text("hello"));

        await Shown(" world again");
        Assert.Equal("Segoe UI", Overlay.Field?.HostFontName);
    }

    [Fact]
    public async Task RetypingTheSuggestionAfterItWasCleared_ResumesIt()
    {
        Engine.Completion = r => r.Prefix == "hello" ? " world again" : "";
        Type("hello");
        await Shown(" world again");
        Hook.Press(Keys.Special(KeyKind.Arrow, 0x25));
        await Shown("");
        Engine.Requests.Clear();

        Type(" ");

        await Shown("world again");
        Assert.Empty(Engine.Requests);
    }
}

