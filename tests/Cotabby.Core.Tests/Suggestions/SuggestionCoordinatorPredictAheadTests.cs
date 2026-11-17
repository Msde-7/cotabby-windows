using Cotabby.Core.Suggestions;

namespace Cotabby.Core.Tests.Suggestions;

public sealed class SuggestionCoordinatorPredictAheadTests : CoordinatorHarness
{
    private readonly TaskCompletionSource _hold = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public SuggestionCoordinatorPredictAheadTests()
    {
        Engine.Hold = _hold;
        Engine.Completion = request => request.Prefix == "hello" ? " world again" : " there";
    }

    private async Task GeneratingFor(string prefix)
    {
        await Eventually(() => Engine.Requests.Any(r => r.Prefix == prefix), $"a request for \"{prefix}\" runs");
        await Task.Delay(30);
    }

    [Fact]
    public async Task TypingWhatTheModelPredicts_KeepsTheRequestAndShowsTheRest()
    {
        Type("hello");
        await GeneratingFor("hello");
        int requests = Engine.Requests.Count;

        Type(" wo");
        await Task.Delay(50);
        _hold.SetResult();

        await Shown("rld again");
        // Nothing was asked for the typed text. Only the continuation after the suggestion may follow.
        Assert.DoesNotContain(Engine.Requests.Skip(requests), r => r.Prefix != "hello world again");
    }

    [Fact]
    public async Task TypingSomethingElse_StartsOver()
    {
        Type("hello");
        await GeneratingFor("hello");

        Type(" x");
        await GeneratingFor("hello x");
        _hold.SetResult();

        await Shown(" there");
    }

    [Fact]
    public async Task TypingThroughThePrediction_AsksForTheNextWords()
    {
        Engine.Completion = request => request.Prefix == "hello" ? " world" : " again";
        Type("hello");
        await GeneratingFor("hello");

        Type(" world");
        await Task.Delay(50);
        _hold.SetResult();

        await GeneratingFor("hello world");
        await Shown(" again");
    }

    [Fact]
    public async Task ADroppedAnswer_StopsKeepingKeys()
    {
        Coordinator.Settings = SuggestionSettings.Default with { SuggestWithinWords = false };
        Engine.Completion = request => request.Prefix == "Hello " ? "world is big" : "again";
        Type("Hello ");
        await GeneratingFor("Hello ");

        // Trimmed by the kept key, the answer would start mid-word, so it is dropped.
        Type("w");
        await Task.Delay(50);
        _hold.SetResult();
        await Task.Delay(100);
        Type("orld ");

        await GeneratingFor("Hello world ");
        await Shown("again");
    }

    [Fact]
    public async Task AppsOwnPrediction_EndsTheWaitForTypedKeys()
    {
        Type("hello");
        await GeneratingFor("hello");
        Hook.Press(Keys.Char(' '));
        Focus.Replace(Focus.Current! with { HostIsPredicting = true });
        _hold.SetResult();
        await Task.Delay(100);

        Focus.Replace(Fields.Text("hello "));
        await Eventually(() => Engine.Requests.Any(r => r.Prefix == "hello "), "typing is asked about again");
        await Shown("there");
    }

    [Fact]
    public async Task TurnedOff_EveryKeyStartsOver()
    {
        Coordinator.Settings = SuggestionSettings.Default with { PredictAheadWhileTyping = false };
        Type("hello");
        await GeneratingFor("hello");

        Type(" wo");

        await GeneratingFor("hello wo");
        _hold.SetResult();
        await Shown(" there");
    }
}
