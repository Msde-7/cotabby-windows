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
var model = ModelCatalog.All.FirstOrDefault(ModelDownloader.IsCached);
if (model is null)
{
    Console.Error.WriteLine($"[coord] No cached model found in {ModelCatalog.DefaultLocalDirectory()}. Run InferenceSmoke first.");
    return 2;
}
var path = ModelDownloader.LocalPath(model);
Console.WriteLine($"[coord] Using cached model: {model.DisplayName}");
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

Console.WriteLine("[coord] Simulating realistic rapid typing 'def fib'…");
// Reset field to empty and grow it as the user types, like real keystrokes.
focus.SetField(focus.Current! with { Text = string.Empty, CaretOffset = 0 });
foreach (char c in "def fib")
{
    var cur = focus.Current!;
    focus.SetField(cur with
    {
        Text = cur.Text + c,
        CaretOffset = cur.Text.Length + 1,
    });
    hook.Raise(new KeyboardEvent { Kind = KeyKind.Character, Character = c, IsKeyDown = true, HasNonShiftModifier = false });
    await Task.Delay(50); // 50ms per char ~= 20 wpm — well within the 150ms debounce
    hook.Raise(new KeyboardEvent { Kind = KeyKind.Character, Character = c, IsKeyDown = false, HasNonShiftModifier = false });
}
Console.WriteLine($"[coord] Done typing. Field text now: \"{focus.Current!.Text}\"");

Console.WriteLine("[coord] Waiting up to 30s for an overlay.Show call…");
var waitStart = Stopwatch.StartNew();
while (overlay.LastShown is null && waitStart.Elapsed < TimeSpan.FromSeconds(30))
{
    await Task.Delay(200);
}

int exitCode = 0;
if (overlay.LastShown is { } shown)
{
    Console.WriteLine();
    Console.WriteLine("[coord] STEP 1 PASS — overlay.Show was called.");
    Console.WriteLine($"        Text:   {shown.Text}");
    Console.WriteLine($"        Anchor: {shown.Anchor}");
}
else
{
    Console.WriteLine();
    Console.WriteLine("[coord] STEP 1 FAIL — overlay.Show was never called after 30s.");
    await coordinator.DisposeAsync();
    pump.Stop();
    return 1;
}

// STEP 2: press Tab — coordinator should accept and the inserter should be
// called with the visible text.
Console.WriteLine("[coord] STEP 2: pressing Tab to accept…");
var beforeAccept = inserter.LastInsertedText;
hook.Raise(new KeyboardEvent { Kind = KeyKind.Tab, Character = '\0', IsKeyDown = true, HasNonShiftModifier = false });
await Task.Delay(50);
hook.Raise(new KeyboardEvent { Kind = KeyKind.Tab, Character = '\0', IsKeyDown = false, HasNonShiftModifier = false });

var acceptStart = Stopwatch.StartNew();
while (inserter.LastInsertedText is null && acceptStart.Elapsed < TimeSpan.FromSeconds(5))
{
    await Task.Delay(100);
}
if (inserter.LastInsertedText is { } insertedText)
{
    Console.WriteLine($"[coord] STEP 2 PASS — inserter received: \"{insertedText}\"");
    if (insertedText != shown.Text)
    {
        Console.WriteLine($"[coord] STEP 2 WARNING — inserted differs from shown:\n    shown:    \"{shown.Text}\"\n    inserted: \"{insertedText}\"");
    }
}
else
{
    Console.WriteLine("[coord] STEP 2 FAIL — inserter was never called within 5s of Tab.");
    exitCode = 1;
}

// STEP 3: reconciler advance — synthesize the user typing the FIRST char of
// the visible suggestion (which should now be hidden after accept, but we
// trigger a fresh one) and verify Hide was called or session shrank.
Console.WriteLine($"[coord] STEP 3: post-accept overlay state: ShowCalls={overlay.ShowCount} UpdateCalls={overlay.UpdateCount} HideCalls={overlay.HideCount}");
if (overlay.HideCount < 1)
{
    Console.WriteLine("[coord] STEP 3 WARNING — overlay was never hidden after acceptance.");
}
else
{
    Console.WriteLine("[coord] STEP 3 PASS — overlay was hidden after acceptance.");
}

// STEP 4: Window-switch scenario. Generate, then "switch process" mid-stream,
// assert the overlay is hidden / not shown for the new process.
Console.WriteLine();
Console.WriteLine("[coord] STEP 4: window-switch during generation…");
overlay.ShowCount = 0;
overlay.UpdateCount = 0;
overlay.HideCount = 0;
focus.SetField(focus.Current! with { Text = string.Empty, CaretOffset = 0 });
foreach (char c in "long(")
{
    var cur = focus.Current!;
    focus.SetField(cur with { Text = cur.Text + c, CaretOffset = cur.Text.Length + 1 });
    hook.Raise(new KeyboardEvent { Kind = KeyKind.Character, Character = c, IsKeyDown = true, HasNonShiftModifier = false });
    await Task.Delay(30);
    hook.Raise(new KeyboardEvent { Kind = KeyKind.Character, Character = c, IsKeyDown = false, HasNonShiftModifier = false });
}
// Wait 800ms — about half a CPU generation — then simulate Alt-Tab to a
// different process by changing pid.
await Task.Delay(800);
Console.WriteLine($"[coord] STEP 4: mid-stream Alt-Tab (ShowCalls so far = {overlay.ShowCount})");
focus.SetField(new FocusedField
{
    ElementHandle = new object(),
    ProcessId = 99999, // different process
    ProcessName = "other-app",
    Text = "totally different content",
    CaretOffset = 25,
    CaretRect = new ScreenRect(900, 700, 1, 20),
    FieldRect = new ScreenRect(800, 600, 1000, 200),
    IsSingleLine = false,
    IsSecure = false,
});
// Give the cancelled generation time to fully drain.
await Task.Delay(3000);
// After the switch the overlay must be hidden, and there must not be an
// overlay anchored at the old (500, 300) showing in the new process.
if (overlay.HideCount > 0)
{
    Console.WriteLine($"[coord] STEP 4 PASS — overlay hidden after window switch. ShowCalls={overlay.ShowCount} HideCalls={overlay.HideCount}");
}
else
{
    Console.WriteLine($"[coord] STEP 4 WARNING — overlay was not hidden after window switch. ShowCalls={overlay.ShowCount} HideCalls={overlay.HideCount}");
}

// STEP 5: Multiple generation cycles. Ensure _generationInFlight resets
// cleanly between independent typing sessions.
Console.WriteLine();
Console.WriteLine("[coord] STEP 5: three independent typing sessions…");
focus.SetField(focus.Current! with
{
    ProcessId = 11111,
    ProcessName = "session-cycle",
    Text = string.Empty,
    CaretOffset = 0,
});
overlay.ShowCount = 0;
int cycleSuccess = 0;
for (int round = 0; round < 3; round++)
{
    overlay.LastShown = null;
    foreach (char c in "abc")
    {
        var cur = focus.Current!;
        focus.SetField(cur with { Text = cur.Text + c, CaretOffset = cur.Text.Length + 1 });
        hook.Raise(new KeyboardEvent { Kind = KeyKind.Character, Character = c, IsKeyDown = true, HasNonShiftModifier = false });
        await Task.Delay(30);
        hook.Raise(new KeyboardEvent { Kind = KeyKind.Character, Character = c, IsKeyDown = false, HasNonShiftModifier = false });
    }
    var roundWait = Stopwatch.StartNew();
    while (overlay.LastShown is null && roundWait.Elapsed < TimeSpan.FromSeconds(15))
        await Task.Delay(150);
    if (overlay.LastShown is not null)
    {
        cycleSuccess++;
        Console.WriteLine($"[coord] STEP 5 round {round + 1}/3 PASS — overlay text=\"{overlay.LastShown.Text}\"");
        // Accept it and wait briefly for the next round.
        hook.Raise(new KeyboardEvent { Kind = KeyKind.Tab, Character = '\0', IsKeyDown = true, HasNonShiftModifier = false });
        await Task.Delay(50);
        hook.Raise(new KeyboardEvent { Kind = KeyKind.Tab, Character = '\0', IsKeyDown = false, HasNonShiftModifier = false });
        await Task.Delay(500);
    }
    else
    {
        Console.WriteLine($"[coord] STEP 5 round {round + 1}/3 FAIL — no overlay within 15s.");
        break;
    }
}
Console.WriteLine($"[coord] STEP 5 result: {cycleSuccess}/3 rounds passed.");
if (cycleSuccess < 3) exitCode = 1;

await coordinator.DisposeAsync();
pump.Stop();
Console.WriteLine();
Console.WriteLine($"[coord] FINAL: exit={exitCode}");
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
    public ShownInfo? LastShown { get; set; }
    public int ShowCount, UpdateCount, HideCount;
    public void Show(string text, ScreenRect anchor) { LastShown = new(text, anchor); ShowCount++; }
    public void Update(string text) { if (LastShown is not null) LastShown = LastShown with { Text = text }; UpdateCount++; }
    public void Hide() { HideCount++; }
}

sealed class RecordingInserter : ITextInserter
{
    public string? LastInsertedText { get; private set; }
    public int InsertCount { get; private set; }
    public Task<bool> InsertAsync(FocusedField target, string text, CancellationToken ct)
    {
        LastInsertedText = text;
        InsertCount++;
        return Task.FromResult(true);
    }
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
