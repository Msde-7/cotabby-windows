namespace Cotabby.Core.Macros;

/// <summary>
/// Offline physical unit conversions such as <c>/10km->mi</c>, <c>/100f->c</c> and
/// <c>/3cup->ml</c>. Both tokens must belong to the same quantity. A cross-quantity request
/// returns null so currency can try the same string next.
/// </summary>
/// <remarks>
/// Tokens resolve through an explicit table so ambiguous abbreviations are unsurprising.
/// <c>m</c> is meters, <c>c</c> is Celsius, and <c>oz</c> is ounce-mass with <c>floz</c> for
/// the fluid ounce. Coefficients are the exact definitions. The inch, foot, yard and mile follow
/// the international yard of 0.9144 m, the ounce, pound and stone follow the international pound
/// of 0.45359237 kg, and the volume units are US customary liquid measures built on the gallon
/// of 231 cubic inches.
/// </remarks>
public sealed class UnitConversionEvaluator : IMacroEvaluator
{
    public MacroResult? Evaluate(string query)
    {
        if (ConversionSeparator.Split(query) is not var (left, right)) return null;
        if (MacroNumber.SplitLeadingNumber(left.Trim()) is not var (value, fromToken)) return null;
        var toToken = right.Trim().ToLowerInvariant();

        if (!Units.TryGetValue(fromToken.ToLowerInvariant(), out var from)
            || !Units.TryGetValue(toToken, out var to)
            || from.Quantity != to.Quantity)
        {
            return null;
        }

        double converted = (value * from.Coefficient + from.Constant - to.Constant) / to.Coefficient;
        return new MacroResult($"{MacroNumber.Format(converted, integerLimit: 1e12, significantDigits: 4)} {toToken}");
    }

    private enum Quantity
    {
        Length,
        Mass,
        Temperature,
        Volume,
    }

    /// <summary>A unit as a linear map onto its quantity's base unit.</summary>
    private sealed record Unit(Quantity Quantity, double Coefficient, double Constant = 0);

    private const double Pound = 0.45359237;
    private const double Gallon = 3.785411784;

    private static readonly Dictionary<string, Unit> Units = BuildUnits();

    private static Dictionary<string, Unit> BuildUnits()
    {
        var units = new Dictionary<string, Unit>();
        void Add(Unit unit, params string[] tokens)
        {
            foreach (var token in tokens) units[token] = unit;
        }

        // Length in meters.
        Add(new(Quantity.Length, 0.001), "mm", "millimeter", "millimeters", "millimetre", "millimetres");
        Add(new(Quantity.Length, 0.01), "cm", "centimeter", "centimeters", "centimetre", "centimetres");
        Add(new(Quantity.Length, 1), "m", "meter", "meters", "metre", "metres");
        Add(new(Quantity.Length, 1000), "km", "kilometer", "kilometers", "kilometre", "kilometres");
        Add(new(Quantity.Length, 0.0254), "in", "inch", "inches");
        Add(new(Quantity.Length, 0.3048), "ft", "foot", "feet");
        Add(new(Quantity.Length, 0.9144), "yd", "yard", "yards");
        Add(new(Quantity.Length, 1609.344), "mi", "mile", "miles");

        // Mass in kilograms.
        Add(new(Quantity.Mass, 0.000001), "mg", "milligram", "milligrams");
        Add(new(Quantity.Mass, 0.001), "g", "gram", "grams");
        Add(new(Quantity.Mass, 1), "kg", "kgs", "kilo", "kilos", "kilogram", "kilograms");
        Add(new(Quantity.Mass, Pound / 16), "oz", "ounce", "ounces");
        Add(new(Quantity.Mass, Pound), "lb", "lbs", "pound", "pounds");
        Add(new(Quantity.Mass, Pound * 14), "st", "stone", "stones");

        // Temperature in kelvin.
        Add(new(Quantity.Temperature, 1, 273.15), "c", "celsius", "centigrade");
        Add(new(Quantity.Temperature, 5.0 / 9, 459.67 * 5 / 9), "f", "fahrenheit");
        Add(new(Quantity.Temperature, 1), "k", "kelvin");

        // Volume in liters.
        Add(new(Quantity.Volume, 0.001), "ml", "milliliter", "milliliters", "millilitre", "millilitres");
        Add(new(Quantity.Volume, 1), "l", "liter", "liters", "litre", "litres");
        Add(new(Quantity.Volume, Gallon / 16), "cup", "cups");
        Add(new(Quantity.Volume, Gallon / 256), "tbsp", "tablespoon", "tablespoons");
        Add(new(Quantity.Volume, Gallon / 768), "tsp", "teaspoon", "teaspoons");
        Add(new(Quantity.Volume, Gallon / 128), "floz");
        Add(new(Quantity.Volume, Gallon), "gal", "gallon", "gallons");
        Add(new(Quantity.Volume, Gallon / 8), "pt", "pint", "pints");
        Add(new(Quantity.Volume, Gallon / 4), "qt", "quart", "quarts");

        return units;
    }
}
