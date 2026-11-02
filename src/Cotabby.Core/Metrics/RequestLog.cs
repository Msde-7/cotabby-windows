using System.Text.Json;

namespace Cotabby.Core.Metrics;

public sealed record RequestLogEntry(DateTimeOffset Timestamp, string ModelName, int LatencyMs);

/// <summary>
/// The most recent model requests, recorded only while performance tracking is on. The file is
/// rewritten after each request, which the cap keeps to a few kilobytes.
/// </summary>
public sealed class RequestLog
{
    public const int Capacity = 100;

    private readonly object _gate = new();
    private readonly string? _path;
    private List<RequestLogEntry> _entries = [];

    /// <param name="path">Where the log is kept between runs, or null to keep it in memory.</param>
    public RequestLog(string? path = null)
    {
        _path = path;
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
            Save();
        }
        Changed?.Invoke();
    }

    public void Clear()
    {
        lock (_gate)
        {
            if (_entries.Count == 0) return;
            _entries = [];
            Save();
        }
        Changed?.Invoke();
    }

    private void Save()
    {
        if (_path is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(_entries));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A log that cannot be written still shows this session's requests.
        }
    }
}
