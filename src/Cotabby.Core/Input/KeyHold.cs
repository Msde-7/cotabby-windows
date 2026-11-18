using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cotabby.Core.Input;

/// <summary>One keyboard event as the hook received it, with what sending it again needs.</summary>
/// <param name="VirtualKey">The virtual key, or <see cref="VkPacket"/> for a Unicode keystroke.</param>
/// <param name="ScanCode">The hardware scan code, or the UTF-16 unit of a <see cref="VkPacket"/> event.</param>
/// <param name="IsKeyUp">True for a release.</param>
/// <param name="IsExtended">True for a key of the extended set, such as the right Ctrl or the arrows.</param>
public readonly record struct RawKey(int VirtualKey, int ScanCode, bool IsKeyUp, bool IsExtended = false)
{
    /// <summary>VK_PACKET, which the touch keyboard and text-sending tools type through.</summary>
    public const int VkPacket = 0xE7;

    public bool IsPacket => VirtualKey == VkPacket;
}

/// <summary>What the hook does with one event before any handler runs.</summary>
public enum KeyRoute
{
    /// <summary>Handlers see it, then the host unless a handler swallows it.</summary>
    Deliver,

    /// <summary>Kept back, unseen by handlers and the host, to be sent again later.</summary>
    Hold,

    /// <summary>
    /// The release of a key a handler swallowed. The hook notes that the key is up, but neither
    /// handlers nor the host see it, since the host never saw the key go down.
    /// </summary>
    Drop,
}

/// <summary>Puts key events back into the system input queue.</summary>
public interface IKeyInjector
{
    /// <summary>
    /// Sends held keys again, marked so the hook knows them for replays: it runs its handlers for
    /// them as for typed keys. Returns how many the system took.
    /// </summary>
    int Replay(ReadOnlySpan<RawKey> keys);

    /// <summary>Sends keys straight to the host: the hook neither holds them nor runs handlers for them.</summary>
    int Pass(ReadOnlySpan<RawKey> keys);
}

/// <summary>
/// The hook-level hold behind <see cref="IKeyboardHook.BeginHold"/>: which events the hook keeps
/// back, and when and in what order they are sent again.
/// </summary>
/// <remarks>
/// <para>
/// The hook asks <see cref="Arrive"/> about every event before its handlers run. While a hold is on,
/// every event the user types is queued and suppressed. Once the last hold is released the queue
/// is sent again with <see cref="IKeyInjector.Replay"/>, and each replay comes back through the hook
/// to its handlers and the host as if typed then.
/// </para>
/// <para>
/// Ordering. The system input queue is first in, first out, and the hook sees its events one at a
/// time in that order. A batch of replays is put at its end, behind any typed event the hook has
/// not reached yet. Such an event was typed after everything in the batch, which the hook had
/// already queued, so it must not reach the host before the batch: until the hook has seen every
/// replay of the batch come back, typed events are still queued, and they go in the next batch.
/// A typed event the hook sees after the last replay came back was behind the batch in the system
/// queue, so it can pass. A key that begins a new hold while a batch is coming back (a replayed
/// Tab that accepts again) is followed by the rest of the batch: those replays are taken back into
/// the queue as they arrive, in their place ahead of anything typed after them. Only one batch is
/// out at a time, so the queue always holds the events not yet handled in the order they were typed.
/// </para>
/// <para>
/// Bounds. A hold that is not released within <see cref="MaxHold"/> lets its keys go, in order.
/// A batch the hook has not seen come back within <see cref="MaxReplayWait"/> is given up on, as
/// when Windows removed the hook. A queue that outgrows <see cref="InitialCapacity"/> lets its
/// keys go early. Nothing is dropped except the release of a swallowed key, and keys the system
/// refused to take.
/// </para>
/// </remarks>
public sealed class KeyHold : IDisposable
{
    /// <summary>The longest one hold keeps typed keys back.</summary>
    public static readonly TimeSpan MaxHold = TimeSpan.FromSeconds(1);

    /// <summary>How long a batch of replays may take to come back through the hook.</summary>
    public static readonly TimeSpan MaxReplayWait = TimeSpan.FromMilliseconds(500);

    /// <summary>How many events the queue holds before it lets them go early.</summary>
    public const int InitialCapacity = 256;

    /// <summary>Past this many events a typed key is handed over unheld rather than lost.</summary>
    public const int MaxCapacity = 4096;

    private readonly object _gate = new();
    private readonly IKeyInjector _injector;
    private readonly TimeProvider _clock;
    private readonly Action<Action> _schedule;
    private readonly ILogger _logger;
    private readonly ITimer _timer;
    private readonly Action _flush;

    // Events not yet handled, in the order they were typed, in _items[_start.._end). The ones sent
    // and not back yet are _flightCount entries from _start + _flightStart. _flightStart is past 0
    // only while replays are taken back for a new hold, which then sit ahead of them.
    private Entry[] _items = new Entry[InitialCapacity];
    private RawKey[] _batch = new RawKey[InitialCapacity];
    private int _start;
    private int _end;
    private int _flightStart;
    private int _flightCount;
    private bool _recalling;
    private bool _sending;

    private int _depth;
    private int _generation;
    private long _holdEndsAt;
    private long _replayDueBy;
    private bool _disposed;

    // Holds whose swallowed key is still down, so its release can be told apart.
    private readonly List<Ticket> _awaitingRelease = new(4);

    // Read without the lock on every key, so the hook's common path stays cheap.
    private volatile bool _engaged;

    public KeyHold(
        IKeyInjector injector, TimeProvider? clock = null, Action<Action>? schedule = null, ILogger? logger = null)
    {
        _injector = injector;
        _clock = clock ?? TimeProvider.System;
        _schedule = schedule ?? (static work => ThreadPool.UnsafeQueueUserWorkItem(static w => w(), work, preferLocal: false));
        _logger = logger ?? NullLogger.Instance;
        _flush = Flush;
        _timer = _clock.CreateTimer(static state => ((KeyHold)state!).OnTimer(), this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>True while any event is held or on its way back.</summary>
    public bool IsHolding => _engaged;

    /// <summary>Events held or on their way back.</summary>
    public int Count
    {
        get { lock (_gate) return _end - _start; }
    }

    /// <summary>Starts a hold. <paramref name="swallowed"/> is the key down the caller swallowed, if any.</summary>
    public IKeyHold Begin(RawKey? swallowed = null)
    {
        lock (_gate)
        {
            var ticket = new Ticket(this, _generation, swallowed is { IsKeyUp: false } down ? down : null);
            if (_disposed)
            {
                ticket.Released = true;
                return ticket;
            }
            _depth++;
            _engaged = true;
            // The replays still on their way were typed after the key that began this hold.
            if (_flightCount > 0) _recalling = true;
            _holdEndsAt = _clock.GetTimestamp() + Ticks(MaxHold);
            if (ticket.Swallowed is { } key) TrackRelease(ticket, key);
            ArmTimer();
            return ticket;
        }
    }

    /// <summary>Decides what the hook does with an event. Called on the hook thread, in the order events arrive.</summary>
    /// <param name="isReplay">True when the event carries the replay mark.</param>
    public KeyRoute Arrive(in RawKey key, bool isReplay)
    {
        if (!isReplay && !_engaged) return KeyRoute.Deliver;
        KeyRoute route;
        bool flush;
        lock (_gate)
        {
            route = isReplay ? ArriveReplay(out flush) : ArriveTyped(key, out flush);
        }
        if (flush) _schedule(_flush);
        return route;
    }

    /// <summary>Lets every held key go now, as when the hook stops.</summary>
    public void ReleaseAll()
    {
        lock (_gate) ForceRelease();
        Flush();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            ForceRelease();
        }
        Flush();
        _timer.Dispose();
    }

    private KeyRoute ArriveReplay(out bool flush)
    {
        flush = false;
        // A replay that outlived its wait was given up on, and it is handled as typed.
        if (_flightCount == 0) return KeyRoute.Deliver;

        KeyRoute route;
        if (_recalling)
        {
            // A hold began after it was sent, and it was typed after the key that began it.
            _flightStart++;
            route = KeyRoute.Hold;
        }
        else
        {
            var entry = _items[_start];
            _items[_start++] = default;
            route = entry.Mark is { Keep: false } mark ? Dropped(mark) : KeyRoute.Deliver;
        }

        if (--_flightCount == 0)
        {
            _recalling = false;
            _flightStart = 0;
            flush = _depth == 0 && _end > _start && !_sending;
            TryDisengage();
        }
        return route;
    }

    private KeyRoute ArriveTyped(in RawKey key, out bool flush)
    {
        flush = false;
        if (!_engaged) return KeyRoute.Deliver;
        var mark = key.IsKeyUp ? MatchRelease(key.VirtualKey) : null;
        if (_end == _items.Length && !MakeRoom())
        {
            _logger.LogWarning("Too many keys were held behind an edit. A key went to the host unheld.");
            return mark is { Keep: false } unkept ? Dropped(unkept) : KeyRoute.Deliver;
        }
        _items[_end++] = new Entry(key, mark);
        // Held while nothing holds, behind replays that have already come back: it needs a batch of its own.
        flush = _depth == 0 && _flightCount == 0 && !_sending;
        return KeyRoute.Hold;
    }

    /// <summary>
    /// Sends the held events again, one batch at a time. A new batch waits until the hook has seen
    /// the last one come back, so a typed event the hook reaches before that is in the next batch.
    /// </summary>
    private void Flush()
    {
        while (true)
        {
            int count;
            lock (_gate)
            {
                if (_sending || _depth > 0 || _flightCount > 0) return;
                count = _end - _start;
                if (count == 0)
                {
                    TryDisengage();
                    return;
                }
                if (_batch.Length < count) _batch = new RawKey[_items.Length];
                for (int i = 0; i < count; i++) _batch[i] = _items[_start + i].Key;
                _flightStart = 0;
                _flightCount = count;
                _sending = true;
                _replayDueBy = _clock.GetTimestamp() + Ticks(MaxReplayWait);
                ArmTimer();
            }

            int sent;
            try
            {
                sent = _injector.Replay(_batch.AsSpan(0, count));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Sending held keys again failed.");
                sent = 0;
            }

            lock (_gate)
            {
                _sending = false;
                if (sent < count)
                {
                    // They will never come back, so nothing waits for them.
                    _logger.LogWarning("The system took {Sent} of {Count} held keys. The rest were lost.", sent, count);
                    int lost = Math.Min(count - sent, _flightCount);
                    RemoveRange(_start + _flightStart + _flightCount - lost, lost);
                    _flightCount -= lost;
                    if (_flightCount == 0)
                    {
                        _recalling = false;
                        _flightStart = 0;
                    }
                }
                // The hook flushes again once the batch is back. If it already is, the hook left that here.
                if (_flightCount > 0) return;
            }
        }
    }

    private void GiveBack(Ticket ticket)
    {
        RawKey down;
        bool withRelease;
        lock (_gate)
        {
            if (ticket.GivenBack || ticket.Swallowed is not { } key) return;
            ticket.GivenBack = true;
            down = key;
            if (ticket.Mark is { } mark)
            {
                // A release still held goes to the host in its place among the held keys.
                withRelease = mark.Dropped;
                mark.Keep = true;
            }
            else
            {
                // A release still to come is kept when it arrives. One already gone by unheld is sent here.
                withRelease = !_awaitingRelease.Contains(ticket);
            }
        }

        // Every key typed after it is held until this hold is released, so it reaches the host first.
        Span<RawKey> keys = [down, down with { IsKeyUp = true }];
        try
        {
            if (_injector.Pass(withRelease ? keys : keys[..1]) == 0)
            {
                _logger.LogWarning("The swallowed key could not be given back to the host.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Giving the swallowed key back to the host failed.");
        }
    }

    private void Release(Ticket ticket)
    {
        lock (_gate)
        {
            if (ticket.Released) return;
            ticket.Released = true;
            // A hold that timed out was already let go.
            if (ticket.Generation != _generation) return;
            if (--_depth > 0) return;
        }
        Flush();
    }

    private void OnTimer()
    {
        bool flush;
        lock (_gate)
        {
            long now = _clock.GetTimestamp();
            if (_depth > 0 && now >= _holdEndsAt)
            {
                _logger.LogWarning("An edit held typed keys for longer than {MaxHold}. They go to the host now.", MaxHold);
                ForceRelease();
            }
            if (_flightCount > 0 && !_sending && now >= _replayDueBy)
            {
                // Windows likely removed the hook, and the keys went to the host without it.
                _logger.LogWarning("{Count} replayed keys did not come back through the keyboard hook.", _flightCount);
                RemoveRange(_start + _flightStart, _flightCount);
                _flightCount = 0;
                _flightStart = 0;
                _recalling = false;
            }
            flush = _depth == 0 && _flightCount == 0;
            ArmTimer();
        }
        if (flush) Flush();
    }

    /// <summary>Lets go of every hold. A hold released later changes nothing.</summary>
    private void ForceRelease()
    {
        if (_depth == 0) return;
        _generation++;
        _depth = 0;
    }

    private void TryDisengage()
    {
        if (_depth > 0 || _end > _start || _sending || _flightCount > 0) return;
        _engaged = false;
        _start = _end = 0;
        // A release that comes once nothing is held passes as typed, as it would have without the hold.
        _awaitingRelease.Clear();
        ArmTimer();
    }

    /// <summary>Points the timer at the nearest deadline, or stops it.</summary>
    private void ArmTimer()
    {
        long due = long.MaxValue;
        if (_depth > 0) due = _holdEndsAt;
        if (_flightCount > 0) due = Math.Min(due, _replayDueBy);
        var wait = due == long.MaxValue
            ? Timeout.InfiniteTimeSpan
            : TimeSpan.FromSeconds(Math.Max(0, due - _clock.GetTimestamp()) / (double)_clock.TimestampFrequency);
        _timer.Change(wait, Timeout.InfiniteTimeSpan);
    }

    private long Ticks(TimeSpan span) => (long)(span.TotalSeconds * _clock.TimestampFrequency);

    private void TrackRelease(Ticket ticket, RawKey down)
    {
        // Everything still queued was typed after the swallowed key, so the first release of the
        // same key is its own. Auto-repeats of it share that release.
        for (int i = _start; i < _end; i++)
        {
            ref var entry = ref _items[i];
            if (!entry.Key.IsKeyUp || entry.Key.VirtualKey != down.VirtualKey) continue;
            entry = entry with { Mark = entry.Mark ?? new ReleaseMark() };
            ticket.Mark = entry.Mark;
            return;
        }
        _awaitingRelease.Add(ticket);
    }

    private ReleaseMark? MatchRelease(int virtualKey)
    {
        ReleaseMark? mark = null;
        for (int i = _awaitingRelease.Count - 1; i >= 0; i--)
        {
            var ticket = _awaitingRelease[i];
            if (ticket.Swallowed!.Value.VirtualKey != virtualKey) continue;
            mark ??= new ReleaseMark();
            // The host got the key down back, so it needs the release too.
            if (ticket.GivenBack) mark.Keep = true;
            ticket.Mark = mark;
            _awaitingRelease.RemoveAt(i);
        }
        return mark;
    }

    private static KeyRoute Dropped(ReleaseMark mark)
    {
        mark.Dropped = true;
        return KeyRoute.Drop;
    }

    /// <summary>Makes room for one more event at the end, letting the holds go when the queue is full.</summary>
    private bool MakeRoom()
    {
        int count = _end - _start;
        if (_start > 0)
        {
            Array.Copy(_items, _start, _items, 0, count);
            Array.Clear(_items, count, _end - count);
            _start = 0;
            _end = count;
            return true;
        }
        if (_depth > 0)
        {
            _logger.LogWarning("{Count} keys are held behind an edit. They go to the host now.", count);
            ForceRelease();
        }
        if (_items.Length >= MaxCapacity) return false;
        Array.Resize(ref _items, _items.Length * 2);
        return true;
    }

    private void RemoveRange(int index, int count)
    {
        if (count <= 0) return;
        Array.Copy(_items, index + count, _items, index, _end - index - count);
        Array.Clear(_items, _end - count, count);
        _end -= count;
    }

    private readonly record struct Entry(RawKey Key, ReleaseMark? Mark);

    /// <summary>The release of a swallowed key. It is dropped unless the key was given back.</summary>
    private sealed class ReleaseMark
    {
        public bool Keep;
        public bool Dropped;
    }

    private sealed class Ticket(KeyHold owner, int generation, RawKey? swallowed) : IKeyHold
    {
        public int Generation { get; } = generation;
        public RawKey? Swallowed { get; } = swallowed;
        public bool Released { get; set; }
        public bool GivenBack { get; set; }
        public ReleaseMark? Mark { get; set; }

        public void GiveBackKey() => owner.GiveBack(this);

        public void Release() => owner.Release(this);
    }
}
