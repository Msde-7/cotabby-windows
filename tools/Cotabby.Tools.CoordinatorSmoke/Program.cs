// Drives the real coordinator and the real local model through a typing session,
// with the keyboard, the focused field and the overlay faked. Exit code 0 means
// every step passed.

using System.Collections.Concurrent;
using System.Diagnostics;
using Cotabby.Core.Focus;
using Cotabby.Core.Input;
using Cotabby.Core.Insertion;
using Cotabby.Core.Models;
using Cotabby.Core.Overlay;
using Cotabby.Core.Suggestions;
using Cotabby.Inference;
using Microsoft.Extensions.Logging;

using var loggers = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Warning).AddSimpleConsole());

var model = ModelCatalog.All.FirstOrDefault(ModelDownloader.IsCached);
if (model is null)
{
    Console.Error.WriteLine($"No cached model in {ModelCatalog.DefaultLocalDirectory()}.");
    return 2;
}

await using var runtime = new LlamaRuntimeManager(loggers.CreateLogger<LlamaRuntimeManager>(), loggers);
await runtime.LoadAsync(model, ModelDownloader.LocalPath(model), CancellationToken.None);
Console.WriteLine($"Model {model.DisplayName} loaded.");

var hook = new FakeHook();
var field = new FakeField();
var overlay = new RecordingOverlay();
using var pump = new PumpContext();
await using var coordinator = new SuggestionCoordinator(
    hook, field, new LlamaSuggestionEngine(runtime, loggers.CreateLogger<LlamaSuggestionEngine>()),
    overlay, field, new SuggestionWorkController(TimeSpan.FromMilliseconds(20)),
    loggers.CreateLogger<SuggestionCoordinator>(), pump);
coordinator.Start();
field.Focus("notepad", 100);

int failures = 0;

// Step 1. Typing prose brings up a suggestion.
const string typed = "Thanks for the update. I will send the report";
var clock = Stopwatch.StartNew();
foreach (char c in typed)
{
    hook.Press(Key(KeyKind.Character, char.ToUpperInvariant(c), c));
    await Task.Delay(10);
    field.Append(c.ToString());
    await Task.Delay(20);
}
string shown = await WaitFor(() => overlay.Text, text => text.Length > 0);
Check("a suggestion appears after typing", shown.Length > 0, $"\"{shown}\" after {clock.ElapsedMilliseconds} ms");

// Step 2. The accept key is swallowed and inserts the first word of what was shown.
bool swallowed = hook.Press(Key(KeyKind.Tab, KeyBinding.VkTab));
string inserted = await WaitFor(() => field.LastInsertion, text => text.Length > 0);
Check("the accept key is swallowed", swallowed);
Check("the first word is inserted", inserted.Length > 0 && shown.StartsWith(inserted), $"\"{inserted}\"");

// Step 3. Accepting everything leaves the field holding the typed text plus the suggestion.
hook.Press(Key(KeyKind.Character, KeyBinding.VkBacktick, '`'));
await WaitFor(() => field.Text, text => text.EndsWith(shown.TrimEnd()));
Check("the whole suggestion lands in the field", field.Text == typed + shown.TrimEnd() || field.Text == typed + shown,
    $"\"{field.Text}\"");

// Step 4. Moving to another app hides whatever is showing.
await WaitFor(() => overlay.Text, text => text.Length > 0, timeoutMs: 5000);
field.Focus("word", 200);
string afterSwitch = await WaitFor(() => overlay.Text, text => text.Length == 0, timeoutMs: 3000);
Check("switching apps hides the suggestion", afterSwitch.Length == 0);

Console.WriteLine(failures == 0 ? "PASS" : $"FAIL ({failures})");
return failures == 0 ? 0 : 1;

void Check(string what, bool passed, string detail = "")
{
    if (!passed) failures++;
    Console.WriteLine($"[{(passed ? "ok" : "FAIL")}] {what} {detail}");
}

static KeyboardEvent Key(KeyKind kind, int virtualKey, char character = '\0') => new()
{
    Kind = kind,
    Character = character,
    HasNonShiftModifier = false,
    IsKeyDown = true,
    VirtualKey = virtualKey,
};

static async Task<string> WaitFor(Func<string> read, Func<string, bool> done, int timeoutMs = 30000)
{
    var clock = Stopwatch.StartNew();
    while (!done(read()) && clock.ElapsedMilliseconds < timeoutMs) await Task.Delay(20);
    return read();
}

sealed class FakeHook : IKeyboardHook
{
    public event EventHandler<KeyEventArgs>? KeyEvent;

    public bool Press(KeyboardEvent ev)
    {
        var args = new KeyEventArgs(ev);
        KeyEvent?.Invoke(this, args);
        return args.Suppress;
    }

    public void Start() { }
    public void Stop() { }
    public void Dispose() { }
}

/// <summary>A text field that behaves like a host app. Typed and inserted text lands in it.</summary>
sealed class FakeField : IFocusTracker, ITextInserter
{
    private readonly object _gate = new();
    private FocusedField? _field;
    private string _lastInsertion = "";

    public event EventHandler<FocusedField?>? FocusChanged;

    public FocusedField? Current
    {
        get { lock (_gate) return _field; }
    }

    public string Text => Current?.Text ?? "";

    public string LastInsertion
    {
        get { lock (_gate) return _lastInsertion; }
    }

    public FocusedField? Refresh() => Current;

    public void Focus(string process, int pid)
    {
        var field = new FocusedField
        {
            ElementHandle = new object(),
            ProcessId = pid,
            ProcessName = process,
            Text = "",
            CaretOffset = 0,
            CaretRect = new ScreenRect(500, 300, 1, 20),
            FieldRect = new ScreenRect(0, 0, 800, 600),
            IsSingleLine = false,
            IsSecure = false,
        };
        lock (_gate) _field = field;
        FocusChanged?.Invoke(this, field);
    }

    public void Append(string text)
    {
        lock (_gate)
        {
            var updated = _field!.Text + text;
            _field = _field with { Text = updated, CaretOffset = updated.Length };
        }
    }

    public Task<bool> InsertAsync(FocusedField target, string text, CancellationToken ct)
    {
        lock (_gate) _lastInsertion = text;
        Append(text);
        return Task.FromResult(true);
    }

    public Task<bool> ReplaceAsync(FocusedField target, int deleteCount, string text, CancellationToken ct)
    {
        lock (_gate)
        {
            var updated = _field!.Text[..^deleteCount] + text;
            _field = _field with { Text = updated, CaretOffset = updated.Length };
        }
        return Task.FromResult(true);
    }

    public void Start() { }
    public void Stop() { }
    public void Dispose() { }
}

sealed class RecordingOverlay : IOverlayPresenter
{
    private volatile string _text = "";

    public string Text => _text;

    public void Show(string text, ScreenRect anchor, bool isCorrection = false) => _text = text;
    public void Update(string text) => _text = text;
    public void Hide() => _text = "";
}

/// <summary>Runs posted work in order on one thread, like a UI dispatcher.</summary>
sealed class PumpContext : SynchronizationContext, IDisposable
{
    private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();

    public PumpContext()
    {
        new Thread(() =>
        {
            SetSynchronizationContext(this);
            foreach (var (callback, state) in _queue.GetConsumingEnumerable()) callback(state);
        })
        { IsBackground = true }.Start();
    }

    public override void Post(SendOrPostCallback d, object? state) => _queue.Add((d, state));

    public void Dispose() => _queue.CompleteAdding();
}
