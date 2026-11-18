using System.Diagnostics;
using Cotabby.Core.Context;
using Cotabby.Core.Input;
using Cotabby.Core.Spelling;
using Cotabby.Core.Suggestions;

namespace Cotabby.Core.Tests.Suggestions;

/// <summary>A coordinator wired to fakes, with helpers that type like a user and wait for the overlay.</summary>
public abstract class CoordinatorHarness : IAsyncDisposable
{
    protected static readonly KeyboardEvent Backtick =
        Keys.Special(KeyKind.Character, KeyBinding.VkBacktick) with { Character = '`' };

    private readonly PumpContext _pump = new();
    private readonly SuggestionWorkController _work = new(TimeSpan.FromMilliseconds(5));

    private protected FakeHook Hook { get; } = new();
    private protected FakeFocus Focus { get; } = new();
    private protected ScriptedEngine Engine { get; } = new();
    private protected RecordingOverlay Overlay { get; } = new();
    private protected RecordingLogger<SuggestionCoordinator> Log { get; } = new();
    protected SuggestionCoordinator Coordinator { get; }
    protected bool BatterySaver { get; set; }

    protected CoordinatorHarness(
        TypoCorrectionAdvisor? typos = null, IClipboardReader? clipboard = null, TimeProvider? clock = null)
    {
        Coordinator = new SuggestionCoordinator(
            Hook, Focus, Engine, Overlay, Focus,
            _work,
            Log, _pump,
            batterySaverOn: () => BatterySaver,
            clock: clock,
            typos: typos,
            clipboard: clipboard);
        Coordinator.Start();
        Focus.Focus(Fields.Text(""));
    }

    public async ValueTask DisposeAsync()
    {
        await Coordinator.DisposeAsync();
        _pump.Dispose();
        GC.SuppressFinalize(this);
    }

    protected void Type(string text)
    {
        foreach (char c in text)
        {
            // The hook sees the key before the host applies it.
            Hook.Press(Keys.Char(c));
            Focus.Append(c.ToString());
        }
    }

    protected static async Task Eventually(Func<bool> condition, string because)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(clock.ElapsedMilliseconds < 5000, "Timed out waiting until " + because);
            await Task.Delay(5);
        }
    }

    protected Task Shown(string text) => Eventually(() => Overlay.Text == text, $"the overlay shows \"{text}\"");

    /// <summary>Asserts that nothing shows once the coordinator has finished everything it started.</summary>
    protected async Task StaysHidden()
    {
        await Settled();
        Assert.Equal("", Overlay.Text);
    }

    /// <summary>Exceptions that escaped work posted to the UI context.</summary>
    protected IReadOnlyCollection<Exception> UiFailures => _pump.Failures;

    /// <summary>Completes once the UI context has handled every key pressed and everything posted so far.</summary>
    protected Task KeysHandled() => _pump.Drained();

    /// <summary>
    /// Waits until the coordinator has nothing left that could change what shows or what lands in
    /// the field: the UI context has run everything posted to it, no request waits or generates
    /// except one parked on the engine's hold, no insertion is under way, and nothing was read,
    /// generated or posted over several looks in a row.
    /// </summary>
    protected async Task Settled()
    {
        var clock = Stopwatch.StartNew();
        (long Posted, int Reads, int Requests)? last = null;
        int quietLooks = 0;
        while (quietLooks < 3)
        {
            Assert.True(clock.ElapsedMilliseconds < 5000, "Timed out waiting for the coordinator to settle");
            await _pump.Drained();
            // Work queued to the thread pool before this has at least started once this runs.
            await Task.Factory.StartNew(
                static () => { }, CancellationToken.None, TaskCreationOptions.PreferFairness, TaskScheduler.Default);
            var stamp = (_pump.Posted, Focus.Refreshes, Engine.Requests.Count);
            bool idle = Engine.Running == 0 && Focus.EditsInFlight == 0 && (_work.IsIdle || Engine.Parked > 0);
            quietLooks = idle && stamp == last ? quietLooks + 1 : 0;
            last = stamp;
            await Task.Delay(10);
        }
    }
}
