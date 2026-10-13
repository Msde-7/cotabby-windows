using Cotabby.Core.Macros;

namespace Cotabby.Core.Tests.Macros;

/// <summary>
/// Results are formatted as an integer when exact, otherwise to four significant digits,
/// and are labelled with the target token as typed (lowercased).
/// </summary>
public class UnitConversionEvaluatorTests
{
    private readonly UnitConversionEvaluator _sut = new();

    [Theory]
    [InlineData("10km->mi", "6.214 mi")]      // length, non-integer result
    [InlineData("1km->m", "1000 m")]          // length, integer result has no decimals
    [InlineData("5ft->in", "60 in")]
    [InlineData("1kg->g", "1000 g")]          // mass
    [InlineData("1 pound to kg", "0.4536 kg")]
    [InlineData("1st->kg", "6.35 kg")]
    [InlineData("100f->c", "37.78 c")]        // temperature is affine, not just a scale
    [InlineData("-40c->f", "-40 f")]          // the one point where both scales agree
    [InlineData("0c->k", "273.1 k")]
    [InlineData("1l->ml", "1000 ml")]         // volume
    [InlineData("1floz->ml", "29.57 ml")]
    [InlineData("3cup->ml", "709.8 ml")]      // US customary cup, a sixteenth of a gallon
    public void ConvertsWithinEachQuantity(string query, string expected)
    {
        Assert.Equal(expected, _sut.Evaluate(query)?.InsertionText);
    }

    // Each large input is a whole number of base units only with the exact coefficient.
    [Theory]
    [InlineData("1000mi->m", "1609344 m")]                // international mile, 1609.344 m
    [InlineData("100000000lb->kg", "45359237 kg")]        // international pound, 0.45359237 kg
    [InlineData("1600000000oz->kg", "45359237 kg")]       // 16 ounces to the pound
    [InlineData("100000000st->kg", "635029318 kg")]       // 14 pounds to the stone
    [InlineData("1000000000gal->l", "3785411784 l")]      // US gallon, 231 cubic inches
    [InlineData("4000000000qt->l", "3785411784 l")]
    [InlineData("8000000000pt->l", "3785411784 l")]
    [InlineData("16000000000cup->l", "3785411784 l")]
    [InlineData("128000000000floz->l", "3785411784 l")]
    [InlineData("256000000000tbsp->l", "3785411784 l")]
    [InlineData("768000000000tsp->l", "3785411784 l")]
    public void CoefficientsAreTheExactDefinitions(string query, string expected)
    {
        Assert.Equal(expected, _sut.Evaluate(query)?.InsertionText);
    }

    [Theory]
    [InlineData("10mi->ft", "52800 ft")]
    [InlineData("1mi->yd", "1760 yd")]
    [InlineData("1lb->oz", "16 oz")]
    [InlineData("1st->lb", "14 lb")]
    [InlineData("1gal->qt", "4 qt")]
    [InlineData("1gal->cup", "16 cup")]
    [InlineData("2cup->pt", "1 pt")]
    [InlineData("1cup->floz", "8 floz")]
    [InlineData("1cup->tbsp", "16 tbsp")]
    [InlineData("1tbsp->tsp", "3 tsp")]
    [InlineData("32f->c", "0 c")]
    [InlineData("212f->c", "100 c")]
    public void RelatedUnitsConvertToWholeNumbers(string query, string expected)
    {
        Assert.Equal(expected, _sut.Evaluate(query)?.InsertionText);
    }

    [Fact]
    public void PreviewMatchesInsertion()
    {
        Assert.Equal(new MacroResult("1000 m"), _sut.Evaluate("1km->m"));
    }

    [Theory]
    [InlineData("10km->mi")]
    [InlineData("10km→mi")]
    [InlineData("10 km to mi")]
    [InlineData("10 KM TO MI")]
    [InlineData("10 km -> mi")]
    public void EverySeparatorParsesIdentically(string query)
    {
        Assert.Equal("6.214 mi", _sut.Evaluate(query)?.InsertionText);
    }

    [Fact]
    public void FullAndBritishUnitNamesAreAccepted()
    {
        Assert.Equal("1000 meters", _sut.Evaluate("1 kilometer to meters")?.InsertionText);
        Assert.Equal("1000 metres", _sut.Evaluate("1 kilometre to metres")?.InsertionText);
        Assert.Equal("37.78 celsius", _sut.Evaluate("100 fahrenheit to celsius")?.InsertionText);
    }

    [Fact]
    public void CrossQuantityReturnsNull()
    {
        Assert.Null(_sut.Evaluate("10km->kg"));
        Assert.Null(_sut.Evaluate("5c->ml"));
    }

    [Fact]
    public void OzIsMassSoFluidVolumeTargetIsCrossQuantity()
    {
        // oz deliberately means ounce-mass and floz is the fluid ounce. Converting oz to a volume
        // must refuse rather than silently treat it as fluid ounces.
        Assert.Null(_sut.Evaluate("1oz->ml"));
        Assert.NotNull(_sut.Evaluate("1oz->g"));
    }

    [Fact]
    public void UnknownTokensReturnNull()
    {
        Assert.Null(_sut.Evaluate("100USD->EUR"));
        Assert.Null(_sut.Evaluate("10min->s"));
    }

    [Fact]
    public void MissingNumberOrTokenReturnsNull()
    {
        Assert.Null(_sut.Evaluate("km->mi"));
        Assert.Null(_sut.Evaluate("10->mi"));
        Assert.Null(_sut.Evaluate("10km->"));
    }

    [Fact]
    public void QueryWithoutSeparatorReturnsNull()
    {
        Assert.Null(_sut.Evaluate("10km mi"));
    }

    [Fact]
    public void LargeAndSmallResultsUseExponentNotation()
    {
        Assert.Equal("1.235e+08 mm", _sut.Evaluate("123456.7891m->mm")?.InsertionText);
        Assert.Equal("1e-06 km", _sut.Evaluate("1mm->km")?.InsertionText);
    }

    [Theory]
    [InlineData("de-DE")]
    [InlineData("fr-FR")]
    public void ParsingAndFormattingIgnoreTheCurrentCulture(string culture)
    {
        // These cultures use a decimal comma. Input and output must still use a point.
        using var scope = new CultureScope(culture);
        Assert.Equal("1500 m", _sut.Evaluate("1.5km->m")?.InsertionText);
        Assert.Equal("6.214 mi", _sut.Evaluate("10km->mi")?.InsertionText);
        Assert.Equal("-40 f", _sut.Evaluate("-40c->f")?.InsertionText);
        Assert.Null(_sut.Evaluate("1,5km->m"));
    }
}
