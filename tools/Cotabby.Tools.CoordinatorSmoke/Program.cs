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
using Cotabby.Core.Tests.Suggestions;
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
var engine = new WatchedEngine(new LlamaSuggestionEngine(runtime, loggers.CreateLogger<LlamaSuggestionEngine>()));
using var pump = new PumpContext();
await using var coordinator = new SuggestionCoordinator(
    hook, field, engine,
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

// Step 5. Typing the words the model is producing keeps its request running, and the rest shows.
const string prompt = "Please let me know if you have any questions about the";
field.Focus("notepad", 300);
await TypeAsync(prompt, 30);
string full = await WaitFor(() => overlay.Text, text => text.Length > 4);
string ahead = full[..Math.Min(4, full.Length)];
int seen = engine.PartialsFor(prompt);
field.Focus("notepad", 301);
await TypeAsync(prompt, 30);
await WaitFor(() => engine.PartialsFor(prompt) > seen ? "y" : "", text => text.Length > 0, timeoutMs: 10000);
await TypeAsync(ahead, 30);
string rest = await WaitFor(() => overlay.Text, text => text.Length > 0);
Check("typing ahead sends no new request", !engine.Prefixes.Any(p => p.StartsWith(prompt) && p.Length > prompt.Length),
    $"typed \"{ahead}\" of \"{full}\"");
Check("the rest of the prediction shows", full.StartsWith(ahead + rest.TrimEnd()) || (ahead + rest).StartsWith(full.TrimEnd()),
    $"\"{rest}\"");

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
    IsKeyDown = true,
    VirtualKey = virtualKey,
};

async Task TypeAsync(string text, int gapMs)
{
    foreach (char c in text)
    {
        hook.Press(Key(KeyKind.Character, char.ToUpperInvariant(c), c));
        await Task.Delay(gapMs / 3);
        field.Append(c.ToString());
        await Task.Delay(gapMs - gapMs / 3);
    }
}

static async Task<string> WaitFor(Func<string> read, Func<string, bool> done, int timeoutMs = 30000)
{
    var clock = Stopwatch.StartNew();
    while (!done(read()) && clock.ElapsedMilliseconds < timeoutMs) await Task.Delay(20);
    return read();
}

/// <summary>The real engine, with the prefixes it was asked for and the partial results it reported.</summary>
sealed class WatchedEngine(ISuggestionEngine inner) : ISuggestionEngine
{
    private readonly ConcurrentDictionary<string, int> _partials = new();

    public ConcurrentQueue<string> Prefixes { get; } = new();

    public bool IsReady => inner.IsReady;
    public bool SpacingIsExact => inner.SpacingIsExact;

    public int PartialsFor(string prefix) => _partials.GetValueOrDefault(prefix);

    public Task<string> GenerateAsync(SuggestionRequest request, CancellationToken ct, Action<string>? onPartial = null)
    {
        Prefixes.Enqueue(request.Prefix);
        return inner.GenerateAsync(request, ct, onPartial is null ? null : partial =>
        {
            _partials.AddOrUpdate(request.Prefix, 1, (_, n) => n + 1);
            onPartial(partial);
        });
    }
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

    public FocusedField? Refresh(bool readHostText = false) => Current;

    public void Focus(string process, int pid)
    {
        var field = new FocusedField
        {
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
