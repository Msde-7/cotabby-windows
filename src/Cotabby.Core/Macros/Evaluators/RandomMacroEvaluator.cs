using System.Globalization;

namespace Cotabby.Core.Macros;

/// <summary>
/// Random and generator macros. Covers <c>/random</c> (<c>/rand</c>, <c>/rnd</c>),
/// <c>/random(n)</c>, <c>/random(a,b)</c>, <c>/dice</c> (<c>/roll</c>, <c>/die</c> and
/// <c>/dN</c> notation like <c>/d20</c>), <c>/coin</c> (<c>/flip</c>) and <c>/uuid</c>
/// (<c>/guid</c>).
/// </summary>
/// <param name="randomSource">Picks an integer between the two inclusive bounds.</param>
/// <param name="uuidSource">Produces a fresh identifier.</param>
public sealed class RandomMacroEvaluator(
    Func<int, int, int>? randomSource = null,
    Func<string>? uuidSource = null) : IMacroEvaluator
{
    private static readonly string[] ParameterizedPrefixes = ["random(", "rand(", "rnd("];

    private readonly Func<int, int, int> _randomSource =
        randomSource ?? ((low, high) => (int)Random.Shared.NextInt64(low, (long)high + 1));

    private readonly Func<string> _uuidSource =
        uuidSource ?? (() => Guid.NewGuid().ToString().ToUpperInvariant());

    public MacroResult? Evaluate(string query)
    {
        var lower = query.ToLowerInvariant();
        switch (lower)
        {
            case "uuid" or "guid":
                return new MacroResult(_uuidSource());
            case "dice" or "die" or "roll":
                return Roll(1, 6);
            case "coin" or "flip" or "coinflip" or "coin-flip":
                return new MacroResult(_randomSource(0, 1) == 0 ? "Heads" : "Tails");
            case "random" or "rand" or "rnd":
                return Roll(0, 100);
            default:
                return DiceSides(lower) is { } sides ? Roll(1, sides) : ParameterizedRandom(lower);
        }
    }

    private MacroResult Roll(int low, int high) =>
        new(_randomSource(low, high).ToString(CultureInfo.InvariantCulture));

    /// <summary>
    /// The side count for <c>dN</c> notation, or null when the token is not <c>d</c> followed
    /// by a positive integer (so <c>/d</c> and <c>/dollar</c> fall through to other families).
    /// </summary>
    private static int? DiceSides(string lower) =>
        lower.StartsWith('d') && MacroNumber.ParseInt(lower.AsSpan(1)) is { } sides && sides >= 1 ? sides : null;

    /// <summary>
    /// Handles <c>random(n)</c> and <c>random(a,b)</c> plus the short prefixes, with integer
    /// arguments and reversed bounds normalized.
    /// </summary>
    private MacroResult? ParameterizedRandom(string lower)
    {
        if (!ParameterizedPrefixes.Any(prefix => lower.StartsWith(prefix, StringComparison.Ordinal)) || !lower.EndsWith(')'))
        {
            return null;
        }

        var inner = lower[(lower.IndexOf('(') + 1)..^1];
        var values = inner
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => MacroNumber.ParseInt(part.Trim()))
            .ToArray();

        return values switch
        {
            [{ } sides] when sides >= 1 => Roll(1, sides),
            [{ } a, { } b] => Roll(Math.Min(a, b), Math.Max(a, b)),
            _ => null,
        };
    }
}
