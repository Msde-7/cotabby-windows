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
        await Task.Delay(50);
        gate.SetResult();

        await Eventually(() => Focus.Current!.Text == "hello a", "the held keys follow the accepted text");
        Assert.False(Hook.Press(Keys.Char('b')), "typing passes through once the insertion is done");
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
        await Task.Delay(100);
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
        await Shown(" three");
        gate.SetResult();

        await Shown("");
        await Task.Delay(100);
        Assert.Equal("hello", Focus.Current!.Text);
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
        await Task.Delay(50);
        Engine.Requests.Clear();

        Hook.Press(Keys.Special(KeyKind.Backspace, 0x08));
        Focus.Replace(Fields.Text("hello"));

        await Shown(" world again");
        Assert.Empty(Engine.Requests);
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

