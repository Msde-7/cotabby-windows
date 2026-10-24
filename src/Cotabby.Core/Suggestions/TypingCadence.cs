using Cotabby.Core.Support;
using Cotabby.Core.Text;

namespace Cotabby.Core.Suggestions;

/// <summary>
/// The rhythm of the letters just typed in one field, with no text kept. Generation may start
/// during a burst, but a suggestion mid-word waits a little longer than the recent gap between
/// letters, so it does not flash up between two keys. Times are passed in so tests need no clock.
/// </summary>
public sealed class TypingCadence
{
    private const int MaxIntervals = 6;
    private const double MinInterval = 0.025;
    private const double MaxInterval = 0.6;
    private const double MinQuietWindow = 0.08;
    private const double MaxQuietWindow = 0.22;

    private readonly Lock _lock = new();
    private readonly List<double> _intervals = [];
    private string? _identity;
    private double? _lastLetterAt;

    /// <summary>Records one key's text in the field named by <paramref name="identity"/>, at <paramref name="seconds"/>.</summary>
    public void Record(string identity, string characters, double seconds)
    {
        if (!double.IsFinite(seconds)) return;
        lock (_lock)
        {
            if (_identity != identity)
            {
                _intervals.Clear();
                _lastLetterAt = null;
                _identity = identity;
            }
            var graphemes = new GraphemeString(characters);
            if (graphemes.Count != 1 || !graphemes[0].IsLetter)
            {
                _lastLetterAt = null;
                return;
            }
            if (_lastLetterAt is { } last)
            {
                double interval = seconds - last;
                if (interval is >= MinInterval and <= MaxInterval)
                {
                    _intervals.Add(interval);
                    if (_intervals.Count > MaxIntervals) _intervals.RemoveAt(0);
                }
                else
                {
                    _intervals.Clear();
                }
            }
            _lastLetterAt = seconds;
        }
    }

    /// <summary>
    /// How much longer a result for <paramref name="precedingText"/> should wait before it shows.
    /// Zero after a finished word, in another field, or once the pause is long enough.
    /// </summary>
    public double RemainingDelay(string identity, string precedingText, double seconds)
    {
        lock (_lock)
        {
            if (_identity != identity || _lastLetterAt is not { } last
                || CaretWordContext.UnfinishedWord(precedingText) is null)
            {
                return 0;
            }
            // Bounded presentation limits, not a debounce. Before a rhythm exists, 80 ms only
            // absorbs a brief hesitation.
            var ordered = _intervals.Order().ToList();
            double quietWindow = ordered.Count >= 2
                ? Math.Min(MaxQuietWindow, Math.Max(MinQuietWindow, ordered[ordered.Count / 2] * 1.2))
                : MinQuietWindow;
            return Math.Max(0, quietWindow - Math.Max(0, seconds - last));
        }
    }
}
