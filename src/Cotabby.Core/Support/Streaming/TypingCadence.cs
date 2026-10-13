namespace Cotabby.Core.Support;

/// <summary>
/// Short-lived, text-free typing rhythm. Generation can begin during a burst, and presentation waits
/// slightly longer than the recent interval between letters. Times are injected seconds on a
/// monotonic clock.
/// </summary>
public sealed class TypingCadence
{
    private const int IntervalCapacity = 6;

    private ulong? _identityKey;
    private double? _lastLetterAt;
    private readonly List<double> _intervals = [];

    public void Record(ulong identityKey, string characters, double time)
    {
        if (!double.IsFinite(time)) return;
        if (_identityKey != identityKey)
        {
            _identityKey = identityKey;
            _lastLetterAt = null;
            _intervals.Clear();
        }
        var typed = new GraphemeString(characters);
        if (typed.Count != 1 || !typed[0].IsLetter)
        {
            _lastLetterAt = null;
            return;
        }
        if (_lastLetterAt is { } last)
        {
            double interval = time - last;
            if (interval is >= 0.025 and <= 0.6)
            {
                _intervals.Add(interval);
                if (_intervals.Count > IntervalCapacity) _intervals.RemoveAt(0);
            }
            else
            {
                _intervals.Clear();
            }
        }
        _lastLetterAt = time;
    }

    public double RemainingDelay(ulong identityKey, string precedingText, double time)
    {
        if (_identityKey != identityKey || _lastLetterAt is not { } last
            || CaretWordBoundary.UnfinishedWord(precedingText) is null)
        {
            return 0;
        }
        var ordered = _intervals.Order().ToList();
        // These are bounded presentation limits, not a global debounce. Before enough rhythm
        // exists, 80 ms absorbs an ordinary brief hesitation. A completed word pays no delay.
        double quietWindow = ordered.Count >= 2
            ? Math.Min(0.22, Math.Max(0.08, ordered[ordered.Count / 2] * 1.2))
            : 0.08;
        return Math.Max(0, quietWindow - Math.Max(0, time - last));
    }
}
