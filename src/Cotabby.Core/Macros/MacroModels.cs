namespace Cotabby.Core.Macros;

/// <summary>
/// A computed macro result. The preview is shown near the caret while the user types
/// and the insertion replaces the typed <c>/query</c> run on accept. The two can differ
/// (arithmetic previews <c>= 10</c> and inserts <c>10</c>).
/// </summary>
public sealed record MacroResult(string PreviewText, string InsertionText)
{
    /// <summary>For macros whose preview and inserted text are identical.</summary>
    public MacroResult(string value) : this(value, value)
    {
    }
}

/// <summary>A pure macro family, deterministic given its injected clock, RNG and rates.</summary>
public interface IMacroEvaluator
{
    /// <summary>
    /// Returns a result when <paramref name="query"/> (the text typed after <c>/</c>, already
    /// trimmed) is a macro this family understands, or null to let the next family try.
    /// </summary>
    MacroResult? Evaluate(string query);
}

/// <summary>
/// Splits a conversion query on the first accepted separator, which is <c>-&gt;</c>, the
/// arrow <c>→</c>, or a space delimited <c>to</c>. Shared by the unit and currency families.
/// </summary>
public static class ConversionSeparator
{
    private static readonly string[] Arrows = ["->", "→"];

    /// <summary>Returns the untrimmed left and right sides, or null without a separator.</summary>
    public static (string Left, string Right)? Split(string query)
    {
        foreach (var arrow in Arrows)
        {
            if (Cut(query, arrow, StringComparison.Ordinal) is { } sides) return sides;
        }
        return Cut(query, " to ", StringComparison.OrdinalIgnoreCase);
    }

    private static (string Left, string Right)? Cut(string query, string separator, StringComparison comparison)
    {
        int index = query.IndexOf(separator, comparison);
        return index < 0 ? null : (query[..index], query[(index + separator.Length)..]);
    }
}
