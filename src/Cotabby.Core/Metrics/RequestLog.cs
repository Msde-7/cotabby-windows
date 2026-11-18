using System.Text.Json;
using Cotabby.Core.Support;

namespace Cotabby.Core.Metrics;

public sealed record RequestLogEntry(DateTimeOffset Timestamp, string ModelName, int LatencyMs);

/// <summary>
/// The most recent model requests, recorded only while performance tracking is on. The cap keeps
/// the file to a few kilobytes. A request is recorded as its generation finishes, which must not
/// wait on the disk, so the file is written on the thread pool a few seconds later, with every
/// request recorded in between, and at dispose.
/// </summary>
public sealed class RequestLog : IDisposable
{
    public const int Capacity = 100;

    private static readonly TimeSpan DefaultSaveDelay = TimeSpan.FromSeconds(5);

    private readonly object _gate = new();
    // Held through a write, so an older snapshot never lands after a newer one.
    private readonly object _writeGate = new();
    private readonly string? _path;
    private readonly TimeSpan _saveDelay;
    private readonly Timer _saveTimer;
    private List<RequestLogEntry> _entries = [];
    private bool _dirty;
    private bool _saveScheduled;
    private bool _disposed;

    /// <param name="path">Where the log is kept between runs, or null to keep it in memory.</param>
    /// <param name="saveDelay">How long after a request the file is written.</param>
    public RequestLog(string? path = null, TimeSpan? saveDelay = null)
    {
        _path = path;
        _saveDelay = saveDelay ?? DefaultSaveDelay;
        _saveTimer = new Timer(_ => Flush());
        try
        {
            if (path is not null && File.Exists(path))
            {
                _entries = [.. (JsonSerializer.Deserialize<List<RequestLogEntry>>(File.ReadAllText(path)) ?? []).TakeLast(Capacity)];
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _entries = [];
        }
    }

    /// <summary>Raised on the recording thread after the log changes.</summary>
    public event Action? Changed;

    public IReadOnlyList<RequestLogEntry> Entries
    {
        get { lock (_gate) return [.. _entries]; }
    }

    public void Record(string modelName, int latencyMs, DateTimeOffset? at = null)
    {
        lock (_gate)
        {
            _entries.Add(new RequestLogEntry(at ?? DateTimeOffset.Now, modelName, latencyMs));
            if (_entries.Count > Capacity) _entries.RemoveRange(0, _entries.Count - Capacity);
            ScheduleSave();
        }
        Changed?.Invoke();
    }

    public void Clear()
    {
        lock (_gate)
        {
            if (_entries.Count == 0) return;
            _entries = [];
            _dirty = _path is not null;
        }
        // Clearing is the user's choice in Settings, so the file empties at once.
        Flush();
        Changed?.Invoke();
    }

    /// <summary>Writes the requests recorded since the last write, if any.</summary>
    public void Flush()
    {
        lock (_writeGate)
        {
            byte[] bytes;
            lock (_gate)
            {
                _saveScheduled = false;
                if (!_dirty || _path is null) return;
                _dirty = false;
                bytes = JsonSerializer.SerializeToUtf8Bytes(_entries);
            }
            try
            {
                AtomicFile.WriteAllBytes(_path, bytes);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A log that cannot be written still shows this session's requests.
            }
        }
    }

    /// <summary>Writes what is pending and stops writing in the background.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _saveTimer.Dispose();
        }
        Flush();
    }

    /// <summary>Arms the write unless one is already due, so a burst of requests is written once. Called under the lock.</summary>
    private void ScheduleSave()
    {
        if (_path is null) return;
        _dirty = true;
        if (_saveScheduled || _disposed) return;
        _saveScheduled = true;
        _saveTimer.Change(_saveDelay, Timeout.InfiniteTimeSpan);
    }
}
