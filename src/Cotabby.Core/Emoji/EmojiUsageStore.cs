using System.Text.Json;

namespace Cotabby.Core.Emoji;

/// <summary>
/// Persists per user emoji usage (recents and frequency) as one JSON file, so the picker ranks a
/// person's usual emoji first and seeds the bare colon panel with them. Not thread safe.
/// </summary>
public sealed class EmojiUsageStore
{
    private const int RecentsCap = 50;

    // Trimming starts above the cap and cuts down to the target, so steady use does not sort the
    // map on every commit.
    private const int FrequencyCap = 300;
    private const int FrequencyTrimTarget = 200;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string _path;
    private readonly Action<Action> _runWrite;
    private readonly object _writeGate = new();
    private long _version;
    private long _writtenVersion;
    private List<string> _recents = [];
    private Dictionary<string, int> _frequency = [];

    private sealed record Persisted(List<string>? Recents, Dictionary<string, int>? Frequency);

    /// <summary>Loads the history stored at <paramref name="path"/>. A missing or corrupt file starts empty.</summary>
    /// <param name="runWrite">
    /// Runs a file write. The default writes at once. A host that records from a thread that must
    /// not block, such as the keyboard hook, passes a scheduler instead.
    /// </param>
    public EmojiUsageStore(string path, Action<Action>? runWrite = null)
    {
        _path = path;
        _runWrite = runWrite ?? (write => write());
        try
        {
            if (JsonSerializer.Deserialize<Persisted>(File.ReadAllBytes(path), JsonOptions)
                is { Recents: { } recents, Frequency: { } frequency })
            {
                _recents = recents;
                _frequency = frequency;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
        }
    }

    /// <summary>
    /// Records one commit of an emoji's primary alias. Moves it to the front of recents, counts it
    /// and persists. Blank input is ignored.
    /// </summary>
    public void Record(string rawAlias)
    {
        var alias = rawAlias.ToLowerInvariant().Trim();
        if (alias.Length == 0) return;

        _recents.Remove(alias);
        _recents.Insert(0, alias);
        if (_recents.Count > RecentsCap) _recents.RemoveRange(RecentsCap, _recents.Count - RecentsCap);
        _frequency[alias] = _frequency.GetValueOrDefault(alias) + 1;
        TrimFrequencyIfNeeded();
        Persist();
    }

    public EmojiUsageSnapshot Snapshot() => new(_recents.ToArray(), new Dictionary<string, int>(_frequency));

    /// <summary>Forgets all recents and frequency.</summary>
    public void Clear()
    {
        _recents = [];
        _frequency = [];
        try
        {
            File.Delete(_path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    // Drops the least used aliases once the map outgrows its cap. Current recents are kept so a
    // just used emoji never loses its favorite ranking to the trim.
    private void TrimFrequencyIfNeeded()
    {
        if (_frequency.Count <= FrequencyCap) return;

        var keepAlways = _recents.ToHashSet();
        int overflow = _frequency.Count - Math.Max(FrequencyTrimTarget, keepAlways.Count);
        var removable = _frequency
            .Where(pair => !keepAlways.Contains(pair.Key))
            .OrderBy(pair => pair.Value)
            .Take(overflow)
            .Select(pair => pair.Key)
            .ToArray();
        foreach (var alias in removable) _frequency.Remove(alias);
    }

    // Best effort. A failed write only costs ranking history.
    private void Persist()
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new Persisted(_recents, _frequency), JsonOptions);
        long version = ++_version;
        _runWrite(() =>
        {
            lock (_writeGate)
            {
                // Scheduled writes can run out of order, and an older state must not win.
                if (version < _writtenVersion) return;
                _writtenVersion = version;
                try
                {
                    if (Path.GetDirectoryName(_path) is { Length: > 0 } directory) Directory.CreateDirectory(directory);
                    File.WriteAllBytes(_path, bytes);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        });
    }
}
