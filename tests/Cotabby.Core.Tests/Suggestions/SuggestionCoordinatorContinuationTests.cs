using Cotabby.Core.Input;

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
        await Task.Delay(50);
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
}
