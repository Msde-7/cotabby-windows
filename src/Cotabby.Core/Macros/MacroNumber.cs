using System.Globalization;

namespace Cotabby.Core.Macros;

/// <summary>
/// Culture invariant number parsing and formatting shared by the macro families. Input
/// always uses <c>.</c> as the decimal separator, whatever the user's regional settings.
/// </summary>
internal static class MacroNumber
{
    public static double? ParseDouble(ReadOnlySpan<char> text)
    {
        const NumberStyles style =
            NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent;
        return double.TryParse(text, style, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value)
            ? value
            : null;
    }

    public static int? ParseInt(ReadOnlySpan<char> text) =>
        int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    /// <summary>
    /// Splits <c>10.5km</c> into its leading number and the trimmed remainder, or returns
    /// null when the text does not start with a number.
    /// </summary>
    public static (double Value, string Remainder)? SplitLeadingNumber(string text)
    {
        int length = 0;
        while (length < text.Length && (char.IsNumber(text[length]) || text[length] is '.' or '-' or '+'))
        {
            length++;
        }
        return ParseDouble(text.AsSpan(0, length)) is { } value ? (value, text[length..].Trim()) : null;
    }

    /// <summary>
    /// Whole values below <paramref name="integerLimit"/> print without a decimal point.
    /// Everything else prints like C's <c>%g</c> with the given significant digits.
    /// </summary>
    public static string Format(double value, double integerLimit, int significantDigits)
    {
        if (value == Math.Round(value) && Math.Abs(value) < integerLimit)
        {
            return ((long)value).ToString(CultureInfo.InvariantCulture);
        }
        return value.ToString("G" + significantDigits, CultureInfo.InvariantCulture).Replace('E', 'e');
    }
}
