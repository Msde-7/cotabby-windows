namespace Cotabby.Win32.Focus;

/// <summary>
/// Holds the latest focus snapshot. Captures run on several threads at once, and one that started
/// before another must not replace the other's result when it finishes later, or a field that lost
/// focus, possibly one that is not secure, would come back as the current one.
/// </summary>
public sealed class CaptureOrder<T> where T : class
{
    private readonly object _gate = new();
    private long _started;
    private long _published;
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

    /// <summary>Clears the current value. Captures already running are dropped when they finish.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _published = Interlocked.Read(ref _started);
            Volatile.Write(ref _current, null);
        }
    }
}
