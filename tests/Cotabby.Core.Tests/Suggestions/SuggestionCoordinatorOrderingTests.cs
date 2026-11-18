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
}
