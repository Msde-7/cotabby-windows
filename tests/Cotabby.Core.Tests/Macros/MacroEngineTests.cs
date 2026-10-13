using System.Globalization;
using Cotabby.Core.Macros;

namespace Cotabby.Core.Tests.Macros;

public class MacroEngineRoutingTests
{
    private static MacroEngine MakeEngine() => MacroEngine.Standard(
        now: () => new DateTime(2026, 6, 4, 12, 0, 0),
        culture: CultureInfo.GetCultureInfo("en-US"),
        randomSource: (low, _) => low);

    /// <summary>Records every query it sees and answers with a fixed result.</summary>
    private sealed class StubEvaluator(MacroResult? result) : IMacroEvaluator
    {
        public List<string> ReceivedQueries { get; } = [];

        public MacroResult? Evaluate(string query)
        {
            ReceivedQueries.Add(query);
            return result;
        }
    }

    [Fact]
    public void RoutesToEachFamily()
    {
        var engine = MakeEngine();
        Assert.Equal("Jun 4, 2026", engine.Evaluate("today")?.InsertionText);
        Assert.Equal("= 10", engine.Evaluate("5+5")?.PreviewText);
        Assert.Equal("6.214 mi", engine.Evaluate("10km->mi")?.InsertionText);
        Assert.Equal("$100.00", engine.Evaluate("136CAD->USD")?.InsertionText);
        Assert.Equal("7", engine.Evaluate("random(7,7)")?.InsertionText);
    }

    [Fact]
    public void RoutesForgivingAliases()
    {
        var engine = MakeEngine();
        Assert.Equal("Jun 4, 2026", engine.Evaluate("tdy")?.InsertionText);
        Assert.Equal("6.214 mi", engine.Evaluate("10 km to mi")?.InsertionText);
        Assert.Equal("€92.00", engine.Evaluate("$100 to eur")?.InsertionText);
        Assert.Equal("1", engine.Evaluate("roll")?.InsertionText);
    }

    [Fact]
    public void SharedWordRoutesByTargetWithUnitBeforeCurrency()
    {
        // "pound" is both a mass unit and a currency alias. The unit family runs first and claims
        // it only when the target is also a mass. Otherwise currency takes over.
        var engine = MakeEngine();
        Assert.Equal("0.4536 kg", engine.Evaluate("1 pound to kg")?.InsertionText);
        Assert.Equal("$1.27", engine.Evaluate("1 pound to usd")?.InsertionText);
    }

    [Fact]
    public void SurroundingSpacesAreTrimmedBeforeRouting()
    {
        Assert.Equal("10", MakeEngine().Evaluate("  5+5  ")?.InsertionText);
    }

    [Fact]
    public void EmptyAndUnknownReturnNull()
    {
        var engine = MakeEngine();
        Assert.Null(engine.Evaluate(""));
        Assert.Null(engine.Evaluate("   "));
        Assert.Null(engine.Evaluate("5"));
        Assert.Null(engine.Evaluate("zzz"));
    }

    [Fact]
    public void InjectedRateTableReachesTheCurrencyFamily()
    {
        var rates = new CurrencyRateTable("test", new Dictionary<string, double> { ["USD"] = 1, ["EUR"] = 0.5 });
        var engine = MacroEngine.Standard(culture: CultureInfo.GetCultureInfo("en-US"), rates: rates);
        Assert.Equal("€5.00", engine.Evaluate("10usd->eur")?.InsertionText);
    }

    [Fact]
    public void FirstMatchingEvaluatorWinsAndLaterOnesAreNotConsulted()
    {
        var declining = new StubEvaluator(null);
        var first = new StubEvaluator(new MacroResult("first"));
        var second = new StubEvaluator(new MacroResult("second"));
        var engine = new MacroEngine([declining, first, second]);

        Assert.Equal("first", engine.Evaluate("q")?.InsertionText);
        Assert.Equal(["q"], declining.ReceivedQueries);
        Assert.Equal(["q"], first.ReceivedQueries);
        Assert.Empty(second.ReceivedQueries);
    }

    [Fact]
    public void EvaluatorsReceiveTheTrimmedQueryAndBlankQueriesNeverReachThem()
    {
        var stub = new StubEvaluator(null);
        var engine = new MacroEngine([stub]);

        Assert.Null(engine.Evaluate(" \t "));
        Assert.Empty(stub.ReceivedQueries);

        engine.Evaluate("  a b  ");
        Assert.Equal(["a b"], stub.ReceivedQueries);
    }
}

public class ConversionSeparatorTests
{
    [Theory]
    [InlineData("10km->mi", "10km", "mi")]
    [InlineData("10km→mi", "10km", "mi")]
    [InlineData("10 km to mi", "10 km", "mi")]
    [InlineData("10 km TO mi", "10 km", "mi")]
    public void SplitsOnEachSupportedSeparator(string query, string left, string right)
    {
        Assert.Equal((left, right), ConversionSeparator.Split(query));
    }

    [Fact]
    public void SidesAreReturnedUntrimmed()
    {
        // Callers own trimming. The parser only cuts at the separator.
        Assert.Equal(("10 km ", " mi"), ConversionSeparator.Split("10 km -> mi"));
    }

    [Fact]
    public void ArrowTakesPrecedenceOverSpacedTo()
    {
        Assert.Equal(("a to b", "c"), ConversionSeparator.Split("a to b->c"));
    }

    [Theory]
    [InlineData("tomato")]
    [InlineData("10kmtomi")]
    [InlineData("10km mi")]
    public void ToMustBeSpaceDelimited(string query)
    {
        Assert.Null(ConversionSeparator.Split(query));
    }
}
