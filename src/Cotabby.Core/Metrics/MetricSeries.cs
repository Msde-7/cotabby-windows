namespace Cotabby.Core.Metrics;

/// <summary>Helpers for drawing a series that can have missing readings.</summary>
public static class MetricSeries
{
    /// <summary>
    /// Splits a series into runs of consecutive readings, keeping each reading's position.
    /// A missing reading ends a run, so the graph leaves a gap instead of drawing a line
    /// across time that was never measured, or a false 0%.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<(int Index, double Value)>> Segments(IReadOnlyList<double?> values)
    {
        var segments = new List<IReadOnlyList<(int Index, double Value)>>();
        List<(int Index, double Value)>? current = null;
        for (int i = 0; i < values.Count; i++)
        {
            if (values[i] is not { } value)
            {
                current = null;
                continue;
            }
            if (current is null)
            {
                current = [];
                segments.Add(current);
            }
            current.Add((i, value));
        }
        return segments;
    }

    /// <summary>The top of a graph's scale: some headroom above the peak, but never below <paramref name="floor"/>.</summary>
    public static double UpperBound(IEnumerable<double> values, double floor) =>
        Math.Max(floor, values.DefaultIfEmpty(0).Max() * 1.2);
}
