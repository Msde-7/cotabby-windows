using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Cotabby.Core.Context;
using Cotabby.Core.Focus;
using Cotabby.Core.Input;
using Cotabby.Core.Insertion;
using Cotabby.Core.Overlay;
using Cotabby.Core.Suggestions;

namespace Cotabby.Core.Tests.Suggestions;

internal static class Fields
{
    public static FocusedField Text(string text, string process = "notepad", int pid = 100, bool singleLine = false) => new()
    {
        ProcessId = pid,
        ProcessName = process,
        Text = text,
        CaretOffset = text.Length,
        CaretRect = new ScreenRect(10, 10, 1, 20),
        FieldRect = new ScreenRect(0, 0, 400, 200),
        IsSingleLine = singleLine,
        IsSecure = false,
    };
}

internal static class Keys
{
    public const int VkEscape = 0x1B;

    public static KeyboardEvent Char(char c) => new()
    {
        Kind = KeyKind.Character,
        Character = c,
        IsKeyDown = true,
        VirtualKey = char.ToUpperInvariant(c),
    };

    public static KeyboardEvent Tab(bool repeat = false) => Special(KeyKind.Tab, KeyBinding.VkTab, repeat);

    public static KeyboardEvent Special(KeyKind kind, int virtualKey = 0, bool repeat = false) => new()
    {
        Kind = kind,
        Character = '\0',
        IsKeyDown = true,
        VirtualKey = virtualKey,
        IsRepeat = repeat,
    };
}

internal sealed class FakeHook : IKeyboardHook
{
    public event EventHandler<KeyEventArgs>? KeyEvent;

    /// <summary>Raises the event and reports whether a handler swallowed the key.</summary>
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

/// <summary>A text field that behaves like a host app: typed and inserted text lands in it.</summary>
internal sealed class FakeFocus : IFocusTracker, ITextInserter
{
    private readonly object _gate = new();
    private FocusedField? _field;

    public event EventHandler<FocusedField?>? FocusChanged;

    public List<string> Insertions { get; } = [];

    public FocusedField? Current
    {
        get { lock (_gate) return _field; }
    }

    public FocusedField? Refresh() => Current;

    public void Focus(FocusedField? field)
    {
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

    public void Move(ScreenRect caret)
    {
        lock (_gate) _field = _field! with { CaretRect = caret };
    }

    public Task<bool> InsertAsync(FocusedField target, string text, CancellationToken ct)
    {
        lock (_gate) Insertions.Add(text);
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

internal sealed class ScriptedEngine : ISuggestionEngine
{
    public Func<SuggestionRequest, string> Completion { get; set; } = _ => " world again";
    public ConcurrentQueue<SuggestionRequest> Requests { get; } = new();
    public bool IsReady { get; set; } = true;
    public bool SpacingIsExact => true;

    public async IAsyncEnumerable<SuggestionChunk> GenerateAsync(
        SuggestionRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        Requests.Enqueue(request);
        await Task.Yield();
        yield return new SuggestionChunk(Completion(request), IsFinal: false);
        yield return new SuggestionChunk(string.Empty, IsFinal: true);
    }
}

/// <summary>A clipboard whose contents only change when the test copies something.</summary>
internal sealed class FakeClipboard : IClipboardReader
{
    private readonly object _gate = new();
    private string? _text;
    private uint _sequenceNumber = 7;
    private int _reads;

    public FakeClipboard(string? text = null) => _text = text;

    public int Reads
    {
        get { lock (_gate) return _reads; }
    }

    public uint SequenceNumber
    {
        get { lock (_gate) return _sequenceNumber; }
    }

    public string? ReadText()
    {
        lock (_gate)
        {
            _reads++;
            return _text;
        }
    }

    public void Copy(string text)
    {
        lock (_gate)
        {
            _text = text;
            _sequenceNumber++;
        }
    }
}

internal sealed class RecordingOverlay : IOverlayPresenter
{
    private readonly object _gate = new();
    private string _text = "";

    public string Text
    {
        get { lock (_gate) return _text; }
    }

    public OverlayStyle Style { get; private set; }

    public bool ShowsCorrection => Style == OverlayStyle.Correction;

    public ScreenRect Anchor { get; private set; }

    public void Show(string text, ScreenRect anchor, OverlayStyle style = OverlayStyle.Inline)
    {
        Style = style;
        Anchor = anchor;
        Update(text);
    }

    public void Update(string text)
    {
        lock (_gate) _text = text;
    }

    public void Hide() => Update("");
}

/// <summary>Runs posted work in order on one dedicated thread, like a UI dispatcher.</summary>
internal sealed class PumpContext : SynchronizationContext, IDisposable
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
