namespace Cotabby.Win32.Focus;

/// <summary>
/// Holds the latest focus snapshot. Captures run on several threads at once, and one that started
/// before another must not replace the other's result when it finishes later, or a field that lost
/// focus, possibly one that is not secure, would come back as the current one.
/// </summary>
public sealed class CaptureOrder<T> where T : class
{
    private readonly object _gate = new();
    private readonly object _deliverGate = new();
    private long _started;
    private long _published;
    private long _delivered;
    private T? _current;

    public T? Current => Volatile.Read(ref _current);

    /// <summary>Called as a capture starts. Its result is published with the number returned.</summary>
    public long Begin() => Interlocked.Increment(ref _started);

    /// <summary>
    /// Makes <paramref name="value"/> current unless a capture that started later was published
    /// first. False when it was dropped.
    /// </summary>
    public bool TryPublish(long sequence, T? value)
    {
        lock (_gate)
        {
            if (sequence <= _published) return false;
            _published = sequence;
            Volatile.Write(ref _current, value);
            return true;
        }
    }

    /// <summary>
    /// Passes the current value to <paramref name="deliver"/> for the capture numbered
    /// <paramref name="sequence"/>, unless a capture that started later was passed on already or a
    /// <see cref="Reset"/> came after this one started. Deliveries never overlap, so listeners get
    /// values in the order their captures started and never end with an older one.
    /// </summary>
    public void Deliver(long sequence, Action<T?> deliver)
    {
        lock (_deliverGate)
        {
            if (sequence <= _delivered) return;
            _delivered = sequence;
            deliver(Current);
        }
    }

    /// <summary>
    /// Clears the current value. Captures already running are dropped when they finish, and a
    /// delivery in progress finishes first.
    /// </summary>
    public void Reset()
    {
        lock (_deliverGate)
        lock (_gate)
        {
            long started = Interlocked.Read(ref _started);
            _published = started;
            _delivered = started;
            Volatile.Write(ref _current, null);
        }
    }
}
