namespace Cotabby.Core.Support;

/// <summary>Formats word counts for compact display.</summary>
public static class WordCountFormatter
{
    /// <summary>
    /// A compact label for a word count, or null when the count should not be shown.
    /// <list type="bullet">
    /// <item>Zero or less gives null.</item>
    /// <item>1 to 999 gives the number itself.</item>
    /// <item>1,000 to 9,999 gives one decimal of thousands, "1.0K" to "10.0K".</item>
    /// <item>10,000 to 999,999 gives whole thousands, truncated.</item>
    /// <item>1,000,000 to 9,999,999 gives one decimal of millions.</item>
    /// <item>Above that gives whole millions, truncated.</item>
    /// </list>
    /// </summary>
    public static string? CompactLabel(long count)
    {
        if (count <= 0) return null;
        if (count < 1_000) return count.ToString();
        if (count < 10_000) return OneDecimalLabel(count, 1_000, "K");
        if (count < 1_000_000) return $"{count / 1_000}K";
        if (count < 10_000_000) return OneDecimalLabel(count, 1_000_000, "M");
        return $"{count / 1_000_000}M";
    }

    /// <summary>
    /// Rounds half up to one decimal using integer tenths. Floating-point formatting would misround
    /// boundaries such as 9,950, which is stored just below 9.95.
    /// </summary>
    private static string OneDecimalLabel(long count, long unit, string suffix)
    {
        long tenths = (count + unit / 20) / (unit / 10);
        return $"{tenths / 10}.{tenths % 10}{suffix}";
    }
}
