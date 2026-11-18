using Cotabby.Core.Input;
using Cotabby.Core.Suggestions;

namespace Cotabby.Core.Tests.Suggestions;

public sealed class SuggestionCoordinatorContinuationTests : CoordinatorHarness
{
    [Fact]
    public async Task TypingThroughTheLastWord_ShowsThePrefetchedContinuation()
    {
        Engine.Completion = request => request.Prefix.EndsWith("world") ? " again" : " world";
        Type("hello");
        await Shown(" world");

        Type(" w");
        await Eventually(() => Engine.Requests.Any(r => r.Prefix == "hello world"), "the continuation is prefetched");
        await Settled();
        int requests = Engine.Requests.Count;
        Type("orld");

        await Shown(" again");
        // The text after "world" was generated once, ahead of time. Showing " again" may start the
        // continuation after it, which is a request for different text.
        Assert.Single(Engine.Requests, r => r.Prefix == "hello world");
        Assert.DoesNotContain(Engine.Requests.Skip(requests), r => r.Prefix != "hello world again");
    }

    [Fact]
    public async Task AContinuationForOtherText_IsNotShown()
    {
        Engine.Completion = request => request.Prefix.EndsWith("world") ? " again" : " world";
        Type("hello");
        await Shown(" world");
        Type(" w");
        await Eventually(() => Engine.Requests.Any(r => r.Prefix == "hello world"), "the continuation is prefetched");
        Engine.Completion = _ => " there";

        Type("x");

        await Shown(" there");
    }

    [Fact]
    public async Task AppsOwnPrediction_KeepsCotabbyQuiet()
    {
        Focus.Replace(Fields.Text("") with { HostIsPredicting = true });

        Type("hello");

        await StaysHidden();
        Assert.Empty(Engine.Requests);
    }

    [Fact]
    public async Task AppsOwnPrediction_HidesAVisibleSuggestion()
    {
        Type("hello");
        await Shown(" world again");

        Focus.Replace(Focus.Current! with { HostIsPredicting = true });

        await Shown("");
    }

    [Fact]
    public async Task Dismissal_HoldsOnlyInTheFieldItWasMadeIn()
    {
        Type("hello");
        await Shown(" world again");
        Hook.Press(Keys.Special(KeyKind.Escape, Keys.VkEscape));
        await Shown("");

        Focus.Focus(Fields.Text("") with { FieldId = "other" });
        Type("hello");

        await Shown(" world again");
    }

    [Fact]
    public async Task ClearingTheSuggestion_StopsItsPrefetchedContinuation()
    {
        Engine.Completion = request => request.Prefix.EndsWith("world") ? " again" : " world";
        Engine.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Engine.Holds = r => r.Prefix == "hello world";
        Type("hello");
        await Shown(" world");
        await Eventually(() => Engine.Requests.Any(r => r.Prefix == "hello world"), "the continuation is prefetched");

        // A click elsewhere in the field: the suggestion goes, and its continuation is of no use.
        Focus.Replace(Fields.Text("hello there"));

        await Shown("");
        await Eventually(() => Engine.Cancelled.Contains("hello world"), "the prefetch is cancelled");
    }

    [Fact]
    public async Task APrefetchForOtherText_IsCancelledWhenTheFieldDisagrees()
    {
        Engine.Completion = request => request.Prefix.EndsWith("world") ? " again" : request.Prefix == "hello" ? " world" : "";
        Engine.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Engine.Holds = r => r.Prefix == "hello world";
        Type("hello");
        await Shown(" world");
        Type(" worl");
        await Eventually(() => Engine.Requests.Any(r => r.Prefix == "hello world"), "the continuation is prefetched");

        // The last key types through the suggestion, but the host shows something else.
        Hook.Press(Keys.Char('d'));
        Focus.Append("D");

        await Eventually(() => Engine.Cancelled.Contains("hello world"), "the unused prefetch is cancelled");
    }

    [Fact]
    public async Task AnEndpoint_GetsNoSpeculativeContinuation()
    {
        Coordinator.Settings = SuggestionSettings.Default with { UsesEndpoint = true };
        Engine.Completion = request => request.Prefix.EndsWith("world") ? " again" : " world";
        Type("hello");
        await Shown(" world");

        await Settled();
        Assert.DoesNotContain(Engine.Requests, r => r.Prefix == "hello world");
    }

    [Fact]
    public async Task AcceptingTheLastChunk_StartsTheContinuationBeforeTheHostShowsIt()
    {
        Engine.Completion = request => request.Prefix == "hello" ? " world again today" : " and more";
        Type("hello");
        await Shown(" world again today");
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Focus.InsertGate = gate;

        Assert.True(Hook.Press(Backtick));

        await Eventually(() => Engine.Requests.Any(r => r.Prefix == "hello world again today"),
            "the continuation generates while the insertion is still on its way");
        gate.SetResult();
        await Shown(" and more");
        Assert.Single(Engine.Requests, r => r.Prefix == "hello world again today");
    }
}

