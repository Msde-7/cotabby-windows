using System.Collections.Concurrent;
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
        IsTextFieldRole = singleLine,
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
    private int _refreshes;
    private int _hostTextReads;
    private int _editsInFlight;

    public event EventHandler<FocusedField?>? FocusChanged;

    public List<string> Insertions { get; } = [];

    public FocusedField? Current
    {
        get { lock (_gate) return _field; }
    }

    /// <summary>How many times the coordinator read the field.</summary>
    public int Refreshes => Volatile.Read(ref _refreshes);

    /// <summary>How many of those reads also asked for the host's text layout, as the read before showing does.</summary>
    public int HostTextReads => Volatile.Read(ref _hostTextReads);

    /// <summary>Insertions that have started and not returned yet.</summary>
    public int EditsInFlight => Volatile.Read(ref _editsInFlight);

    /// <summary>The font the host reports, which only a read for its text layout carries.</summary>
    public string? HostFontName { get; set; }

    public FocusedField? Refresh(bool readHostText = false)
    {
        Interlocked.Increment(ref _refreshes);
        if (!readHostText) return Current;
        Interlocked.Increment(ref _hostTextReads);
        return HostFontName is { } font ? Current! with { HostFontName = font } : Current;
    }

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

    /// <summary>Changes the field without a focus event, as a mouse click inside the same app does.</summary>
    public void Replace(FocusedField field)
    {
        lock (_gate) _field = field;
    }

    public void Move(ScreenRect caret)
    {
        lock (_gate) _field = _field! with { CaretRect = caret };
    }

    /// <summary>When set, insertions wait here before they land, like a slow host.</summary>
    public TaskCompletionSource? InsertGate { get; set; }

    /// <summary>When true, edits are accepted but never show in the field, like a host that drops them.</summary>
    public bool IgnoresEdits { get; set; }

    /// <summary>When true, inserted line breaks read back as CR LF, as in Notepad and RichEdit.</summary>
    public bool StoresCrLf { get; set; }

    /// <summary>How many of the next insertions are rejected, as when the foreground window changed.</summary>
    public int FailingInserts { get; set; }

    public async Task<bool> InsertAsync(FocusedField target, string text, CancellationToken ct)
    {
        Interlocked.Increment(ref _editsInFlight);
        try
        {
            if (InsertGate is { } gate) await gate.Task.WaitAsync(ct);
            lock (_gate)
            {
                if (FailingInserts > 0)
                {
                    FailingInserts--;
                    return false;
                }
            }
            lock (_gate) Insertions.Add(text);
            if (!IgnoresEdits) Append(StoresCrLf ? text.Replace("\n", "\r\n") : text);
            return true;
        }
        finally
        {
            Interlocked.Decrement(ref _editsInFlight);
        }
    }

    public Task<bool> ReplaceAsync(FocusedField target, int deleteCount, string text, CancellationToken ct)
    {
        if (IgnoresEdits) return Task.FromResult(true);
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

    /// <summary>When set, a decode reports its whole text as a partial, then waits here before it finishes.</summary>
    public TaskCompletionSource? Hold { get; set; }

    /// <summary>Which requests <see cref="Hold"/> applies to. All of them by default.</summary>
    public Func<SuggestionRequest, bool> Holds { get; set; } = _ => true;

    /// <summary>Prefixes of the requests that were cancelled while they generated.</summary>
    public ConcurrentQueue<string> Cancelled { get; } = new();

    /// <summary>Prefixes of the requests that got their partial out, or their answer when nothing held them.</summary>
    public ConcurrentQueue<string> Reached { get; } = new();

    private int _running;
    private int _parked;

    /// <summary>Generations under way that are not waiting on <see cref="Hold"/>.</summary>
    public int Running => Volatile.Read(ref _running);

    /// <summary>Generations waiting on <see cref="Hold"/>.</summary>
    public int Parked => Volatile.Read(ref _parked);

    public async Task<string> GenerateAsync(SuggestionRequest request, CancellationToken ct, Action<string>? onPartial = null)
    {
        Interlocked.Increment(ref _running);
        try
        {
            Requests.Enqueue(request);
            await Task.Yield();
            if (Hold is { } hold && Holds(request))
            {
                onPartial?.Invoke(Completion(request));
                Reached.Enqueue(request.Prefix);
                Interlocked.Increment(ref _parked);
                Interlocked.Decrement(ref _running);
                try
                {
                    await hold.Task.WaitAsync(ct);
                }
                catch (OperationCanceledException)
                {
                    Cancelled.Enqueue(request.Prefix);
                    throw;
                }
                finally
                {
                    Interlocked.Increment(ref _running);
                    Interlocked.Decrement(ref _parked);
                }
            }
            else
            {
                Reached.Enqueue(request.Prefix);
            }
            return Completion(request);
        }
        finally
        {
            Interlocked.Decrement(ref _running);
        }
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

    /// <summary>The field the last suggestion was drawn for.</summary>
    public FocusedField? Field { get; private set; }

    public void Show(string text, ScreenRect anchor, OverlayStyle style = OverlayStyle.Inline, FocusedField? field = null)
    {
        Style = style;
        Anchor = anchor;
        Field = field;
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
    private long _posted;

    public PumpContext()
    {
        new Thread(() =>
        {
            SetSynchronizationContext(this);
            foreach (var (callback, state) in _queue.GetConsumingEnumerable()) callback(state);
        })
        { IsBackground = true }.Start();
    }

    /// <summary>How many callbacks were posted so far.</summary>
    public long Posted => Interlocked.Read(ref _posted);

    public override void Post(SendOrPostCallback d, object? state)
    {
        Interlocked.Increment(ref _posted);
        _queue.Add((d, state));
    }

    /// <summary>Completes once every callback posted before it has run. It does not count as posted.</summary>
    public Task Drained()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Add((static state => ((TaskCompletionSource)state!).SetResult(), done));
        return done.Task;
    }

    public void Dispose() => _queue.CompleteAdding();
}

/// <summary>A clock that only moves when the test advances it.</summary>
internal sealed class ManualClock : TimeProvider
{
    private long _milliseconds = 1_000_000;

    public override long TimestampFrequency => 1000;

    public override long GetTimestamp() => Interlocked.Read(ref _milliseconds);

    public void Advance(TimeSpan by) => Interlocked.Add(ref _milliseconds, (long)by.TotalMilliseconds);
}
