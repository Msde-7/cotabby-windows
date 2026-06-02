// Coordinator end-to-end smoke test. Bypasses Windows: fakes IKeyboardHook
// and IFocusTracker, uses the REAL LlamaSuggestionEngine + real
// SuggestionCoordinator + a recording IOverlayPresenter. Verifies the full
// pipeline produces a visible suggestion for synthesized typing.
//
// Pass: exit 0, prints the suggestion text the overlay was asked to show.
// Fail: exit 1, prints why.

using System.Diagnostics;
using Cotabby.Core.Focus;
using Cotabby.Core.Input;
using Cotabby.Core.Insertion;
using Cotabby.Core.Models;
using Cotabby.Core.Overlay;
using Cotabby.Core.Suggestions;
using Cotabby.Inference;
using Microsoft.Extensions.Logging;

var sw = Stopwatch.StartNew();
using var lf = LoggerFactory.Create(b =>
{
    b.SetMinimumLevel(LogLevel.Information);
    b.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss.fff "; });
});

Console.WriteLine("[coord] Loading model…");
await using var runtime = new LlamaRuntimeManager(lf.CreateLogger<LlamaRuntimeManager>());
var model = ModelCatalog.All[0];
var path = ModelDownloader.LocalPath(model);
if (!ModelDownloader.IsCached(model))
{
    Console.Error.WriteLine($"[coord] Model not cached at {path}. Run InferenceSmoke first.");
    return 2;
}
await runtime.LoadAsync(model, path, CancellationToken.None);
var engine = new LlamaSuggestionEngine(runtime, lf.CreateLogger<LlamaSuggestionEngine>());
Console.WriteLine($"[coord] Engine ready in {sw.ElapsedMilliseconds} ms.");

// Build a single-threaded synchronization context so the coordinator's Post()
// behaves like the WPF dispatcher would: serialized continuations on one thread.
var pump = new SingleThreadSyncContext();
var pumpThread = new Thread(pump.Run) { IsBackground = true, Name = "CoordPump" };
pumpThread.Start();
await pump.YieldToContextAsync();

// Fakes.
var focus = new FakeFocusTracker();
var hook = new FakeKeyboardHook();
var inserter = new RecordingInserter();
var overlay = new RecordingOverlay();
var work = new SuggestionWorkController(TimeSpan.FromMilliseconds(150));

await using var coordinator = new SuggestionCoordinator(
    hook, focus, engine, overlay, inserter, work,
    lf.CreateLogger<SuggestionCoordinator>(),
    SynchronizationContext.Current!);
coordinator.Start();

// Scenario: user types "def fib" into a Notepad-like field.
focus.SetField(new FocusedField
{
    ElementHandle = new object(),
    ProcessId = 12345,
    ProcessName = "notepad",
    Text = "def fib",
    CaretOffset = 7,
    CaretRect = new ScreenRect(500, 300, 1, 20),
    FieldRect = new ScreenRect(0, 0, 800, 600),
    IsSingleLine = false,
    IsSecure = false,
});

Console.WriteLine("[coord] Simulating typing 'def fib'…");
foreach (char c in "def fib")
{
    // Each keystroke updates the field as if the host had absorbed the char,
    // then fires the hook event into the coordinator.
    var cur = focus.Current!;
    var newText = cur.Text + (cur.Text.EndsWith(c.ToString()) ? "" : "");
    // Actually advance the field's text/caret to reflect the synthesized typing.
    focus.SetField(cur with
    {
        Text = cur.Text.Length < 7 ? cur.Text + c : cur.Text, // seeded with full text already
    });
    hook.Raise(new KeyboardEvent { Kind = KeyKind.Character, Character = c, IsKeyDown = true, HasNonShiftModifier = false });
    await Task.Delay(20);
    hook.Raise(new KeyboardEvent { Kind = KeyKind.Character, Character = c, IsKeyDown = false, HasNonShiftModifier = false });
}

Console.WriteLine("[coord] Waiting up to 30s for an overlay.Show call…");
var waitStart = Stopwatch.StartNew();
while (overlay.LastShown is null && waitStart.Elapsed < TimeSpan.FromSeconds(30))
{
    await Task.Delay(200);
}

int exitCode;
if (overlay.LastShown is { } shown)
{
    Console.WriteLine();
    Console.WriteLine("[coord] PASS — overlay.Show was called.");
    Console.WriteLine($"        Text:   {shown.Text}");
    Console.WriteLine($"        Anchor: {shown.Anchor}");
    Console.WriteLine($"        ShowCalls={overlay.ShowCount} UpdateCalls={overlay.UpdateCount} HideCalls={overlay.HideCount}");
    exitCode = 0;
}
else
{
    Console.WriteLine();
    Console.WriteLine("[coord] FAIL — overlay.Show was never called after 30s.");
    Console.WriteLine($"        ShowCalls={overlay.ShowCount} UpdateCalls={overlay.UpdateCount} HideCalls={overlay.HideCount}");
    exitCode = 1;
}

await coordinator.DisposeAsync();
pump.Stop();
return exitCode;

// ---------------------------------------------------------------------------

sealed class FakeFocusTracker : IFocusTracker
{
    private FocusedField? _current;
    public FocusedField? Current => _current;
    public event EventHandler<FocusedField?>? FocusChanged;
    public void SetField(FocusedField? f) { _current = f; FocusChanged?.Invoke(this, f); }
    public FocusedField? Refresh() => _current;
    public void Start() { }
    public void Stop() { }
    public void Dispose() { }
}

sealed class FakeKeyboardHook : IKeyboardHook
{
    public event EventHandler<KeyEventArgs>? KeyEvent;
    public void Start() { }
    public void Stop() { }
    public void Dispose() { }
    public void Raise(KeyboardEvent ev) => KeyEvent?.Invoke(this, new KeyEventArgs(ev));
}

sealed class RecordingOverlay : IOverlayPresenter
{
    public record ShownInfo(string Text, ScreenRect Anchor);
    public ShownInfo? LastShown { get; private set; }
    public int ShowCount, UpdateCount, HideCount;
    public void Show(string text, ScreenRect anchor) { LastShown = new(text, anchor); ShowCount++; }
    public void Update(string text) { if (LastShown is not null) LastShown = LastShown with { Text = text }; UpdateCount++; }
    public void Hide() { HideCount++; }
}

sealed class RecordingInserter : ITextInserter
{
    public Task<bool> InsertAsync(FocusedField target, string text, CancellationToken ct) => Task.FromResult(true);
}

sealed class SingleThreadSyncContext : SynchronizationContext
{
    private readonly System.Collections.Concurrent.BlockingCollection<(SendOrPostCallback, object?)> _q = new();
    private volatile bool _running = true;

    public override void Post(SendOrPostCallback d, object? state) => _q.Add((d, state));
    public override void Send(SendOrPostCallback d, object? state) => _q.Add((d, state));

    public void Run()
    {
        SetSynchronizationContext(this);
        while (_running)
        {
            if (_q.TryTake(out var work, 1000))
            {
                try { work.Item1(work.Item2); }
                catch (Exception ex) { Console.Error.WriteLine($"pump exception: {ex}"); }
            }
        }
    }

    public Task YieldToContextAsync()
    {
        var tcs = new TaskCompletionSource();
        Post(_ => tcs.SetResult(), null);
        return tcs.Task;
    }

    public void Stop() => _running = false;
}
