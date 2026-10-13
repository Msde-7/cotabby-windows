using System.Globalization;

namespace Cotabby.Core.Macros;

/// <summary>
/// Tries the macro families in priority order and returns the first match. Dates and
/// random are keyword based, unit and currency both key off the conversion separator but
/// each declines the other's tokens, and arithmetic is the catch-all.
/// </summary>
public sealed class MacroEngine(IReadOnlyList<IMacroEvaluator> evaluators)
{
    /// <summary>The production engine. Every argument defaults to the live system source.</summary>
    /// <param name="now">Local wall-clock time.</param>
    /// <param name="culture">Culture for rendered dates, times and currency amounts.</param>
    /// <param name="randomSource">Picks an integer between the two inclusive bounds.</param>
    /// <param name="rates">Exchange rates for the currency family.</param>
    public static MacroEngine Standard(
        Func<DateTime>? now = null,
        CultureInfo? culture = null,
        Func<int, int, int>? randomSource = null,
        CurrencyRateTable? rates = null) =>
        new(
        [
            new DateMacroEvaluator(now ?? (() => DateTime.Now), culture),
            new RandomMacroEvaluator(randomSource),
            new UnitConversionEvaluator(),
            new CurrencyEvaluator(culture, rates),
            new ArithmeticEvaluator(),
        ]);

    /// <summary>Returns the result for the typed query (without the <c>/</c>), or null.</summary>
    public MacroResult? Evaluate(string query)
    {
        var trimmed = query.Trim();
        if (trimmed.Length == 0) return null;
        foreach (var evaluator in evaluators)
        {
            if (evaluator.Evaluate(trimmed) is { } result) return result;
        }
        return null;
    }
}
