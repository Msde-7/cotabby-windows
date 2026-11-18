using System.Collections.Concurrent;
using Cotabby.Core.Context;
using Cotabby.Core.Focus;
using Cotabby.Core.Input;
using Cotabby.Core.Insertion;
using Cotabby.Core.Overlay;
using Cotabby.Core.Suggestions;
using Microsoft.Extensions.Logging;

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

/// <summary>
/// A keyboard hook over a model of the system input queue. One thread takes the queued events in
/// order, as the raw input thread hands them to the hook: typed keys, keys held behind an accept
/// and sent again, and keys given back. It holds keys with the real <see cref="KeyHold"/>.
/// </summary>
internal sealed class FakeHook : IKeyboardHook, IKeyInjector
{
    private readonly BlockingCollection<Queued> _input = new();
    private readonly ConcurrentDictionary<int, KeyboardEvent> _typed = new();
    private readonly KeyHold _hold;
    private readonly int _threadId;
    private int _nextId;
    private int _unhandled;

    // The key handlers are looking at, so a hold begun in a handler knows which key it swallowed.
    private RawKey? _raising;

    private enum Source { Typed, Replay, Passed }

    private sealed record Queued(RawKey Key, Source Source, bool Reported, TaskCompletionSource<bool>? Done);

    public FakeHook()
    {
        _hold = new KeyHold(this, schedule: work => work());
        var thread = new Thread(Run) { IsBackground = true, Name = "FakeHook input" };
        thread.Start();
        _threadId = thread.ManagedThreadId;
    }

    public event EventHandler<KeyEventArgs>? KeyEvent;

    /// <summary>
    /// Told about each key that reaches the host, on the input thread, except keys sent with
    /// <see cref="Press"/> that pass straight through: the caller applies those itself.
    /// </summary>
    public Action<KeyboardEvent>? HostReceives { get; set; }

    /// <summary>Every key the host received, in order.</summary>
    public ConcurrentQueue<KeyboardEvent> HostKeys { get; } = new();

    /// <summary>True while keys are held behind an edit, or any key has not reached the hook yet.</summary>
    public bool IsHolding => _hold.IsHolding || Volatile.Read(ref _unhandled) > 0;

    /// <summary>
    /// Types a key and reports whether it was kept from the host: swallowed by a handler, or held
    /// to be sent later. A key that passes straight through is left for the caller to apply.
    /// </summary>
    public bool Press(KeyboardEvent ev) => Send(ev, reported: false);

    /// <summary>Types a key the host applies through <see cref="HostReceives"/> whenever it gets it.</summary>
    public bool Hit(KeyboardEvent ev) => Send(ev, reported: true);

    public void Start() { }
    public void Stop() { }

    public void Dispose()
    {
        _input.CompleteAdding();
        _hold.Dispose();
    }

    public IKeyHold BeginHold() =>
        _hold.Begin(Environment.CurrentManagedThreadId == _threadId ? _raising : null);

    int IKeyInjector.Replay(ReadOnlySpan<RawKey> keys) => Inject(keys, Source.Replay);

    int IKeyInjector.Pass(ReadOnlySpan<RawKey> keys) => Inject(keys, Source.Passed);

    private bool Send(KeyboardEvent ev, bool reported)
    {
        int id = Interlocked.Increment(ref _nextId);
        _typed[id] = ev;
        var done = new TaskCompletionSource<bool>();
        Interlocked.Increment(ref _unhandled);
        _input.Add(new Queued(new RawKey(ev.VirtualKey, id, !ev.IsKeyDown), Source.Typed, reported, done));
        // A blocking wait, so typing does not depend on a free thread-pool thread, as a direct call did not.
        if (!done.Task.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("The hook did not take the key.");
        return done.Task.Result;
    }

    private int Inject(ReadOnlySpan<RawKey> keys, Source source)
    {
        foreach (var key in keys)
        {
            Interlocked.Increment(ref _unhandled);
            if (!_input.TryAdd(new Queued(key, source, Reported: true, Done: null)))
            {
                Interlocked.Decrement(ref _unhandled);
                return 0;
            }
        }
        return keys.Length;
    }

    private void Run()
    {
        foreach (var item in _input.GetConsumingEnumerable())
        {
            bool kept;
            try
            {
                kept = Handle(item);
            }
            catch (Exception ex)
            {
                Interlocked.Decrement(ref _unhandled);
                item.Done?.SetException(ex);
                continue;
            }
            Interlocked.Decrement(ref _unhandled);
            item.Done?.SetResult(kept);
        }
    }

    /// <summary>Returns true when the key was kept from the host.</summary>
    private bool Handle(Queued item)
    {
        // The id travels in the scan code, as a real event's data travels in its fields.
        var ev = _typed[item.Key.ScanCode] with { IsKeyDown = !item.Key.IsKeyUp };
        if (item.Source == Source.Passed)
        {
            ToHost(ev, item.Reported);
            return false;
        }
        if (_hold.Arrive(item.Key, item.Source == Source.Replay) != KeyRoute.Deliver) return true;

        ev = ev with { IsReplay = item.Source == Source.Replay };
        var args = new KeyEventArgs(ev);
        _raising = item.Key;
        try
        {
            KeyEvent?.Invoke(this, args);
        }
        finally
        {
            _raising = null;
        }
        if (args.Suppress) return true;
        ToHost(ev, item.Reported);
        return false;
    }

    private void ToHost(KeyboardEvent ev, bool reported)
    {
        HostKeys.Enqueue(ev);
        if (reported) HostReceives?.Invoke(ev);
    }
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

    /// <summary>When set, a read for the host's text layout throws it, as a host that stopped answering can.</summary>
    public Exception? HostTextReadFailure { get; set; }

    public FocusedField? Refresh(bool readHostText = false)
    {
        Interlocked.Increment(ref _refreshes);
        if (!readHostText) return Current;
        if (HostTextReadFailure is { } failure) throw failure;
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

    /// <summary>Deletes the character before the caret, as Backspace does.</summary>
    public void DeleteBack()
    {
        lock (_gate)
        {
            if (_field!.Text.Length == 0) return;
            var updated = _field.Text[..^1];
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

    /// <summary>How long each insertion takes, like a host that pastes slowly.</summary>
    public TimeSpan InsertDelay { get; set; }

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
            if (InsertDelay > TimeSpan.Zero) await Task.Delay(InsertDelay, ct);
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

    /// <summary>When set, the next Hide throws it after clearing the text.</summary>
    public Exception? HideFailure { get; set; }

    public void Hide()
    {
        Update("");
        if (HideFailure is { } failure)
        {
            HideFailure = null;
            throw failure;
        }
    }
}

/// <summary>Keeps every log entry for the test to look at.</summary>
internal sealed class RecordingLogger<T> : ILogger<T>
{
    public ConcurrentQueue<(LogLevel Level, Exception? Exception, string Message)> Entries { get; } = new();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Entries.Enqueue((logLevel, exception, formatter(state, exception)));
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
            foreach (var (callback, state) in _queue.GetConsumingEnumerable())
            {
                // The app's dispatcher logs a failed callback and carries on, so this does too.
                try
                {
                    callback(state);
                }
                catch (Exception ex)
                {
                    Failures.Enqueue(ex);
                }
            }
        })
        { IsBackground = true }.Start();
    }

    /// <summary>Exceptions that escaped posted callbacks.</summary>
    public ConcurrentQueue<Exception> Failures { get; } = new();

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

    /// <summary>Every delay and timer asked of this clock, in order. They still run on real time.</summary>
    public ConcurrentQueue<TimeSpan> Timers { get; } = new();

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        Timers.Enqueue(dueTime);
        return System.CreateTimer(callback, state, dueTime, period);
    }
}
