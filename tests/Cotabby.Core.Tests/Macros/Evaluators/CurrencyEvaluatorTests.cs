using System.Globalization;
using Cotabby.Core.Macros;

namespace Cotabby.Core.Tests.Macros;

/// <summary>
/// Rates are units per USD, so every conversion crosses through USD. Custom rate tables
/// isolate the cross-rate math from the bundled, periodically refreshed values.
/// </summary>
public class CurrencyEvaluatorTests
{
    private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("en-US");
    private readonly CurrencyEvaluator _sut = new(Culture);

    [Fact]
    public void SameCurrencyIsIdentity()
    {
        Assert.Equal("$100.00", _sut.Evaluate("100USD->USD")?.InsertionText);
    }

    [Fact]
    public void CrossRateGoesThroughUsd()
    {
        // 136 CAD / 1.36 (CAD per USD) = 100 USD.
        Assert.Equal("$100.00", _sut.Evaluate("136CAD->USD")?.InsertionText);
    }

    [Fact]
    public void TargetCurrencyFormattingFollowsTheTargetCode()
    {
        Assert.Equal("€92.00", _sut.Evaluate("100USD->EUR")?.InsertionText);
        Assert.Equal("£79.00", _sut.Evaluate("100USD->GBP")?.InsertionText);
        // JPY and KRW have no minor unit, so the decimals are dropped entirely.
        Assert.Equal("¥15,100", _sut.Evaluate("100USD->JPY")?.InsertionText);
        Assert.Equal("₩1,360", _sut.Evaluate("1USD->KRW")?.InsertionText);
    }

    [Fact]
    public void DollarCurrenciesOtherThanUsdAreDisambiguated()
    {
        Assert.Equal("CA$34.00", _sut.Evaluate("$25 to cad")?.InsertionText);
        Assert.Equal("A$151.00", _sut.Evaluate("100usd->aud")?.InsertionText);
    }

    [Fact]
    public void CodeWithoutSymbolIsShownAsTheCodeWithAGap()
    {
        Assert.Equal("CHF 90.00", _sut.Evaluate("100usd->chf")?.InsertionText);
    }

    [Fact]
    public void PreviewMatchesInsertion()
    {
        Assert.Equal(new MacroResult("€92.00"), _sut.Evaluate("100USD->EUR"));
    }

    [Fact]
    public void InjectedRateTableDrivesTheCrossRate()
    {
        var table = new CurrencyRateTable(
            "test",
            new Dictionary<string, double> { ["USD"] = 1, ["EUR"] = 0.5, ["GBP"] = 0.25 });
        var custom = new CurrencyEvaluator(Culture, table);
        Assert.Equal("€5.00", custom.Evaluate("10 usd to eur")?.InsertionText);
        // EUR to GBP never touches a direct pair. 10 EUR / 0.5 = 20 USD, * 0.25 = 5 GBP.
        Assert.Equal("£5.00", custom.Evaluate("10EUR->GBP")?.InsertionText);
    }

    [Fact]
    public void CodeMissingFromTableReturnsNull()
    {
        var table = new CurrencyRateTable("test", new Dictionary<string, double> { ["USD"] = 1 });
        Assert.Null(new CurrencyEvaluator(Culture, table).Evaluate("100USD->EUR"));
    }

    [Theory]
    [InlineData("100us->eur")]
    [InlineData("$100 to eur")]
    [InlineData("$ 100 to eur")]
    [InlineData("100 dollars to euros")]
    [InlineData("100usd->€")]
    [InlineData("100 USD to EUR")]
    public void AliasesSymbolsAndCaseVariantsResolveToTheSameCode(string query)
    {
        Assert.Equal("€92.00", _sut.Evaluate(query)?.InsertionText);
    }

    [Fact]
    public void MultiCharacterSymbolResolvesAsTrailingToken()
    {
        // c$ is a two-character alias, so it only resolves after the amount. The leading-symbol
        // path inspects a single character.
        Assert.Equal("$100.00", _sut.Evaluate("136 c$ to usd")?.InsertionText);
    }

    [Fact]
    public void AmbiguousCurrencyWordsAreDeliberatelyUnresolved()
    {
        // kr and krona are shared by several countries, so the macro refuses to guess.
        Assert.Null(_sut.Evaluate("100 kr to usd"));
        Assert.Null(_sut.Evaluate("100 krona to usd"));
    }

    [Theory]
    [InlineData("100XXX->USD")]
    [InlineData("100USDX->EUR")]
    [InlineData("USD->EUR")]
    [InlineData("100USD EUR")]
    [InlineData("$->EUR")]
    [InlineData("$abc->EUR")]
    public void UnknownOrMalformedCodesReturnNull(string query)
    {
        Assert.Null(_sut.Evaluate(query));
    }

    [Fact]
    public void RateLookupIsCaseInsensitive()
    {
        Assert.Equal(1.0, CurrencyRateTable.Bundled.Rate("usd"));
        Assert.Equal(CurrencyRateTable.Bundled.Rate("EUR"), CurrencyRateTable.Bundled.Rate("eur"));
        Assert.Null(CurrencyRateTable.Bundled.Rate("XXX"));
    }

    [Fact]
    public void BundledTableIsDated()
    {
        Assert.Equal("2026-06", CurrencyRateTable.Bundled.AsOf);
    }

    [Fact]
    public void AmountParsingIgnoresTheCurrentCulture()
    {
        // The amount always uses a decimal point, even on a decimal comma system.
        using var scope = new CultureScope("de-DE");
        Assert.Equal("€1.38", _sut.Evaluate("1.5usd->eur")?.InsertionText);
        Assert.Equal("€1.38", _sut.Evaluate("$1.5 to eur")?.InsertionText);
        Assert.Null(_sut.Evaluate("1,5usd->eur"));
    }

    [Fact]
    public void OutputFollowsTheInjectedCultureLayout()
    {
        var german = new CurrencyEvaluator(CultureInfo.GetCultureInfo("de-DE"));
        Assert.Equal("1.380,00 €", german.Evaluate("1500usd->eur")?.InsertionText);
    }

    [Fact]
    public void DefaultCultureIsTheCurrentCulture()
    {
        using var scope = new CultureScope("de-DE");
        Assert.Equal("92,00 €", new CurrencyEvaluator().Evaluate("100usd->eur")?.InsertionText);
    }
}
