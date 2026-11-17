namespace Cotabby.Core.Text;

internal static class EditDistance
{
    /// <summary>
    /// Bounded Damerau optimal-string-alignment distance, which allows an adjacent transposition.
    /// Returns -1 when the distance exceeds <paramref name="maxDistance"/>.
    /// </summary>
    public static int DamerauOsa<T>(ReadOnlySpan<T> source, ReadOnlySpan<T> target, int maxDistance)
        where T : IEquatable<T>
    {
        if (source.Length == 0) return target.Length <= maxDistance ? target.Length : -1;
        if (target.Length == 0) return source.Length <= maxDistance ? source.Length : -1;
        if (Math.Abs(source.Length - target.Length) > maxDistance) return -1;

        int width = target.Length + 1;
        Span<int> rows = width <= 128 ? stackalloc int[3 * width] : new int[3 * width];
        var previousPrevious = rows[..width];
        var previous = rows.Slice(width, width);
        var current = rows[(2 * width)..];
        for (int column = 0; column <= target.Length; column++) previous[column] = column;

        for (int row = 1; row <= source.Length; row++)
        {
            current[0] = row;
            int rowMinimum = row;
            for (int column = 1; column <= target.Length; column++)
            {
                int cost = source[row - 1].Equals(target[column - 1]) ? 0 : 1;
                int value = Math.Min(
                    Math.Min(previous[column] + 1, current[column - 1] + 1), previous[column - 1] + cost);
                if (row > 1 && column > 1
                    && source[row - 1].Equals(target[column - 2]) && source[row - 2].Equals(target[column - 1]))
                {
                    value = Math.Min(value, previousPrevious[column - 2] + cost);
                }
                current[column] = value;
                rowMinimum = Math.Min(rowMinimum, value);
            }
            // Later rows can only add to the running minimum, so a row over budget ends the search.
            if (rowMinimum > maxDistance) return -1;
            var recycled = previousPrevious;
            previousPrevious = previous;
            previous = current;
            current = recycled;
        }

        int result = previous[target.Length];
        return result <= maxDistance ? result : -1;
    }
}
