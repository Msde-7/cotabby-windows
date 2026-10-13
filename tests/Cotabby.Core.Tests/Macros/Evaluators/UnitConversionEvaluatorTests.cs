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
    [InlineData("3cup->ml", "720 ml")]
    public void ConvertsWithinEachQuantity(string query, string expected)
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
