using Cotabby.Core.Suggestions;
using Xunit;

namespace Cotabby.Core.Tests;

public class SuggestionWorkControllerTests
{
    [Fact]
    public async Task Submit_RunsAfterDebounce()
    {
        await using var c = new SuggestionWorkController(TimeSpan.FromMilliseconds(20));
        var fired = false;
        await c.Submit(_ => { fired = true; return Task.CompletedTask; });
        Assert.True(fired);
    }

    [Fact]
    public async Task Submit_CancelsPriorWork()
    {
        // Long debounce ensures the first work never enters its body before the
        // second submit replaces it.
        await using var c = new SuggestionWorkController(TimeSpan.FromMilliseconds(200));

        bool firstBodyEntered = false;
        bool secondBodyEntered = false;

        var first = c.Submit(_ =>
        {
            firstBodyEntered = true;
            return Task.CompletedTask;
        });

        // Submit a second within the debounce window — should kill the first
        // before its debounce completes, so its body never runs.
        await Task.Delay(20);
        var second = c.Submit(_ =>
        {
            secondBodyEntered = true;
            return Task.CompletedTask;
        });

        await Task.WhenAll(first, second);
        Assert.False(firstBodyEntered);
        Assert.True(secondBodyEntered);
    }

    [Fact]
    public async Task Cancel_StopsScheduledWork()
    {
        await using var c = new SuggestionWorkController(TimeSpan.FromMilliseconds(100));
        bool fired = false;
        var task = c.Submit(_ => { fired = true; return Task.CompletedTask; });
        c.Cancel();
        await task;
        Assert.False(fired);
    }
}
