using System.Diagnostics;
using Cotabby.Core.Input;
using Cotabby.Core.Spelling;
using Cotabby.Core.Suggestions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cotabby.Core.Tests.Suggestions;

/// <summary>A coordinator wired to fakes, with helpers that type like a user and wait for the overlay.</summary>
public abstract class CoordinatorHarness : IAsyncDisposable
{
    protected static readonly KeyboardEvent Backtick =
        Keys.Special(KeyKind.Character, KeyBinding.VkBacktick) with { Character = '`' };

    private readonly PumpContext _pump = new();

    private protected FakeHook Hook { get; } = new();
    private protected FakeFocus Focus { get; } = new();
    private protected ScriptedEngine Engine { get; } = new();
    private protected RecordingOverlay Overlay { get; } = new();
    protected SuggestionCoordinator Coordinator { get; }
    protected bool BatterySaver { get; set; }

    protected CoordinatorHarness(TypoCorrectionAdvisor? typos = null)
    {
        Coordinator = new SuggestionCoordinator(
            Hook, Focus, Engine, Overlay, Focus,
            new SuggestionWorkController(TimeSpan.FromMilliseconds(5)),
            NullLogger<SuggestionCoordinator>.Instance, _pump,
            batterySaverOn: () => BatterySaver,
            typos: typos);
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

    protected async Task StaysHidden()
    {
        await Task.Delay(150);
        Assert.Equal("", Overlay.Text);
    }
}
