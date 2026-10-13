using System.Globalization;

namespace Cotabby.Core.Macros;

/// <summary>
/// Currency conversion macros such as <c>/123.45CAD->USD</c>, <c>/100 usd to eur</c>,
/// <c>/$100 to eur</c> and <c>/100 dollars to yen</c>. Tokens resolve through an alias
/// table on top of any 3-letter ISO code. Rates come from an offline table and never touch
/// the network. The result uses the culture's currency layout with the target's symbol.
/// </summary>
public sealed class CurrencyEvaluator(CultureInfo? culture = null, CurrencyRateTable? table = null) : IMacroEvaluator
{
    private readonly NumberFormatInfo _numberFormat = (culture ?? CultureInfo.CurrentCulture).NumberFormat;
    private readonly CurrencyRateTable _table = table ?? CurrencyRateTable.Bundled;

    public MacroResult? Evaluate(string query)
    {
        if (ConversionSeparator.Split(query) is not var (left, right)
            || ParseAmount(left) is not var (amount, fromToken)
            || ResolveCode(fromToken) is not { } fromCode
            || ResolveCode(right.Trim()) is not { } toCode
            || _table.Rate(fromCode) is not { } fromRate
            || _table.Rate(toCode) is not { } toRate)
        {
            return null;
        }

        // Rates are units per USD, so cross through USD.
        double converted = amount / fromRate * toRate;
        return new MacroResult(FormatCurrency(converted, toCode));
    }

    /// <summary>
    /// Parses the amount and source token from the left side, accepting a leading symbol
    /// (<c>$100</c>) or a trailing code, word or short form (<c>100usd</c>, <c>100 dollars</c>).
    /// </summary>
    private static (double Amount, string Token)? ParseAmount(string left)
    {
        var trimmed = left.Trim();
        if (trimmed.Length == 0) return null;

        var first = trimmed[..1];
        if (Aliases.ContainsKey(first))
        {
            return MacroNumber.ParseDouble(trimmed.AsSpan(1).Trim()) is { } amount ? (amount, first) : null;
        }
        return MacroNumber.SplitLeadingNumber(trimmed);
    }

    /// <summary>
    /// Resolves a token through the alias table first, then accepts any 3-letter code as-is
    /// (the rate table validates it downstream).
    /// </summary>
    private static string? ResolveCode(string token)
    {
        if (Aliases.TryGetValue(token.ToLowerInvariant(), out var code)) return code;
        return token.Length == 3 ? token.ToUpperInvariant() : null;
    }

    /// <summary>
    /// Names, symbols and short forms. Words shared across countries (kr, krona, krone, lira,
    /// koruna, dinar) are deliberately omitted so the macro returns nothing rather than
    /// silently converting the wrong currency.
    /// </summary>
    private static readonly Dictionary<string, string> Aliases = new()
    {
        ["us"] = "USD", ["usa"] = "USD", ["dollar"] = "USD", ["dollars"] = "USD", ["buck"] = "USD", ["bucks"] = "USD", ["$"] = "USD",
        ["euro"] = "EUR", ["euros"] = "EUR", ["€"] = "EUR",
        ["pound"] = "GBP", ["pounds"] = "GBP", ["quid"] = "GBP", ["sterling"] = "GBP", ["£"] = "GBP",
        ["yen"] = "JPY", ["¥"] = "JPY",
        ["yuan"] = "CNY", ["rmb"] = "CNY", ["renminbi"] = "CNY",
        ["rupee"] = "INR", ["rupees"] = "INR", ["₹"] = "INR",
        ["peso"] = "MXN", ["pesos"] = "MXN",
        ["real"] = "BRL", ["reais"] = "BRL", ["r$"] = "BRL",
        ["rand"] = "ZAR",
        ["won"] = "KRW", ["₩"] = "KRW",
        ["ruble"] = "RUB", ["rubles"] = "RUB", ["rouble"] = "RUB", ["₽"] = "RUB",
        ["baht"] = "THB", ["฿"] = "THB",
        ["shekel"] = "ILS", ["shekels"] = "ILS", ["₪"] = "ILS",
        ["forint"] = "HUF",
        ["zloty"] = "PLN", ["zł"] = "PLN",
        ["ringgit"] = "MYR",
        ["rupiah"] = "IDR",
        ["dirham"] = "AED", ["dirhams"] = "AED",
        ["riyal"] = "SAR", ["rial"] = "SAR",
        ["franc"] = "CHF", ["francs"] = "CHF",
        ["₱"] = "PHP",
        ["c$"] = "CAD", ["a$"] = "AUD", ["hk$"] = "HKD", ["nz$"] = "NZD", ["nt$"] = "TWD", ["s$"] = "SGD",
    };

    /// <summary>Display symbols. A code without an entry is shown as the code itself.</summary>
    private static readonly Dictionary<string, string> Symbols = new()
    {
        ["USD"] = "$", ["EUR"] = "€", ["GBP"] = "£", ["JPY"] = "¥", ["CAD"] = "CA$", ["AUD"] = "A$",
        ["CNY"] = "CN¥", ["INR"] = "₹", ["MXN"] = "MX$", ["BRL"] = "R$", ["KRW"] = "₩", ["HKD"] = "HK$",
        ["NZD"] = "NZ$", ["ILS"] = "₪", ["TWD"] = "NT$", ["PHP"] = "₱",
    };

    private static readonly HashSet<string> CodesWithoutMinorUnit = ["JPY", "KRW"];

    private string FormatCurrency(double value, string code)
    {
        var format = (NumberFormatInfo)_numberFormat.Clone();
        var symbol = Symbols.GetValueOrDefault(code, code);
        if (format.CurrencyPositivePattern == 0 && char.IsLetter(symbol[^1]))
        {
            // A bare code in front of the amount reads better with a gap (CHF 90.00).
            symbol += ' ';
        }
        format.CurrencySymbol = symbol;
        format.CurrencyDecimalDigits = CodesWithoutMinorUnit.Contains(code) ? 0 : 2;
        return value.ToString("C", format);
    }
}

/// <summary>
/// Offline, approximate exchange rates as units of each currency per one US dollar.
/// </summary>
/// <param name="ratesPerUsd">Rates keyed by uppercase ISO code.</param>
public sealed class CurrencyRateTable(IReadOnlyDictionary<string, double> ratesPerUsd)
{
    public double? Rate(string code) =>
        ratesPerUsd.TryGetValue(code.ToUpperInvariant(), out var rate) ? rate : null;

    /// <summary>The table shipped with the app. Refreshed only when the app is updated.</summary>
    public static CurrencyRateTable Bundled { get; } = new(
        new Dictionary<string, double>
        {
            ["USD"] = 1.0, ["EUR"] = 0.92, ["GBP"] = 0.79, ["JPY"] = 151.0, ["CAD"] = 1.36, ["AUD"] = 1.51,
            ["CHF"] = 0.90, ["CNY"] = 7.24, ["INR"] = 83.4, ["MXN"] = 17.1, ["BRL"] = 5.05, ["ZAR"] = 18.6,
            ["KRW"] = 1360.0, ["SGD"] = 1.35, ["HKD"] = 7.82, ["NZD"] = 1.63, ["SEK"] = 10.5, ["NOK"] = 10.7,
            ["DKK"] = 6.87, ["PLN"] = 3.95, ["TRY"] = 32.1, ["RUB"] = 90.0, ["AED"] = 3.67, ["SAR"] = 3.75,
            ["THB"] = 36.5, ["IDR"] = 15800.0, ["MYR"] = 4.70, ["PHP"] = 56.5, ["CZK"] = 23.2, ["HUF"] = 360.0,
            ["ILS"] = 3.70, ["TWD"] = 32.3,
        });
}
