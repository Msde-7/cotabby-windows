using Cotabby.Core.Macros;

namespace Cotabby.Core.Tests.Macros;

/// <summary>
/// The injected RNG is a recorder. It returns a configurable value and remembers the range
/// the evaluator asked for, which is what proves <c>/d20</c> rolls 1 to 20.
/// </summary>
public class RandomMacroEvaluatorTests
{
    private readonly List<(int Low, int High)> _requestedRanges = [];
    private Func<int, int, int> _pick = (low, _) => low;

    private RandomMacroEvaluator MakeSut() => new(
        (low, high) =>
        {
            _requestedRanges.Add((low, high));
            return _pick(low, high);
        },
        () => "FIXED-UUID");

    [Theory]
    [InlineData("random", 0, 100)]
    [InlineData("rand", 0, 100)]
    [InlineData("rnd", 0, 100)]
    [InlineData("dice", 1, 6)]
    [InlineData("die", 1, 6)]
    [InlineData("roll", 1, 6)]
    [InlineData("coin", 0, 1)]
    [InlineData("flip", 0, 1)]
    [InlineData("coinflip", 0, 1)]
    [InlineData("coin-flip", 0, 1)]
    public void KeywordMacrosRequestTheirDocumentedRanges(string query, int low, int high)
    {
        Assert.NotNull(MakeSut().Evaluate(query));
        Assert.Equal([(low, high)], _requestedRanges);
    }

    [Fact]
    public void NumericResultsInsertTheRolledValue()
    {
        _pick = (_, high) => high;
        var sut = MakeSut();
        Assert.Equal("100", sut.Evaluate("random")?.InsertionText);
        Assert.Equal("6", sut.Evaluate("dice")?.InsertionText);
    }

    [Fact]
    public void PreviewAndInsertionAreIdentical()
    {
        Assert.Equal(new MacroResult("1", "1"), MakeSut().Evaluate("dice"));
    }

    [Fact]
    public void CoinMapsZeroToHeadsAndOneToTails()
    {
        Assert.Equal("Heads", MakeSut().Evaluate("coin")?.InsertionText);
        _pick = (_, high) => high;
        Assert.Equal("Tails", MakeSut().Evaluate("flip")?.InsertionText);
    }

    [Fact]
    public void UuidAndGuidUseTheInjectedSourceWithoutTouchingTheRng()
    {
        var sut = MakeSut();
        Assert.Equal("FIXED-UUID", sut.Evaluate("uuid")?.InsertionText);
        Assert.Equal("FIXED-UUID", sut.Evaluate("guid")?.InsertionText);
        Assert.Empty(_requestedRanges);
    }

    [Fact]
    public void KeywordsAreCaseInsensitive()
    {
        var sut = MakeSut();
        Assert.Equal("FIXED-UUID", sut.Evaluate("UUID")?.InsertionText);
        Assert.Equal("Heads", sut.Evaluate("Coin")?.InsertionText);
    }

    [Fact]
    public void DiceNotationRollsOneThroughSides()
    {
        Assert.Equal("1", MakeSut().Evaluate("d20")?.InsertionText);
        Assert.Equal([(1, 20)], _requestedRanges);
    }

    [Theory]
    [InlineData("d0")]
    [InlineData("d")]
    [InlineData("dollar")]
    [InlineData("d-4")]
    public void DiceNotationRejectsNonPositiveSidesAndBareD(string query)
    {
        // These are not dice and must fall through so other families can try.
        Assert.Null(MakeSut().Evaluate(query));
        Assert.Empty(_requestedRanges);
    }

    [Fact]
    public void SingleArgumentRollsOneThroughN()
    {
        Assert.Equal("1", MakeSut().Evaluate("random(5)")?.InsertionText);
        Assert.Equal([(1, 5)], _requestedRanges);
    }

    [Fact]
    public void TwoArgumentsNormalizeReversedBounds()
    {
        Assert.Equal("3", MakeSut().Evaluate("random(9, 3)")?.InsertionText);
        Assert.Equal([(3, 9)], _requestedRanges);
    }

    [Fact]
    public void TwoArgumentsAcceptNegativeBounds()
    {
        Assert.Equal("-3", MakeSut().Evaluate("random(-3,-1)")?.InsertionText);
        Assert.Equal([(-3, -1)], _requestedRanges);
    }

    [Fact]
    public void ShortFormPrefixesAcceptArguments()
    {
        var sut = MakeSut();
        Assert.Equal("1", sut.Evaluate("rand(4)")?.InsertionText);
        Assert.Equal("7", sut.Evaluate("rnd(7,7)")?.InsertionText);
        Assert.Equal([(1, 4), (7, 7)], _requestedRanges);
    }

    [Theory]
    [InlineData("random(abc)")]
    [InlineData("random(0)")]
    [InlineData("random(-2)")]
    [InlineData("random()")]
    [InlineData("random(1,2,3)")]
    [InlineData("random(1,x)")]
    [InlineData("random(5")]
    [InlineData("random(1.5)")]
    [InlineData("random(1, ,2)")]
    public void InvalidArgumentsReturnNullWithoutRolling(string query)
    {
        Assert.Null(MakeSut().Evaluate(query));
        Assert.Empty(_requestedRanges);
    }

    [Fact]
    public void UnrelatedQueryReturnsNull()
    {
        Assert.Null(MakeSut().Evaluate("today"));
    }

    [Fact]
    public void NegativeResultsUseAnInvariantMinusSign()
    {
        // Swedish formats negative numbers with U+2212 instead of the ASCII hyphen.
        using var scope = new CultureScope("sv-SE");
        Assert.Equal("-3", MakeSut().Evaluate("random(-3,-1)")?.InsertionText);
    }

    [Fact]
    public void DefaultRandomSourceStaysWithinInclusiveBounds()
    {
        var sut = new RandomMacroEvaluator();
        var seen = new HashSet<string>();
        for (int i = 0; i < 200; i++)
        {
            seen.Add(sut.Evaluate("random(2,3)")!.InsertionText);
        }
        Assert.Equal(["2", "3"], seen.Order());
        Assert.Equal("2147483647", sut.Evaluate("random(2147483647,2147483647)")?.InsertionText);
    }

    [Fact]
    public void DefaultUuidSourceProducesAnUppercaseGuid()
    {
        var text = new RandomMacroEvaluator().Evaluate("uuid")!.InsertionText;
        Assert.True(Guid.TryParseExact(text, "D", out _));
        Assert.Equal(text.ToUpperInvariant(), text);
    }
}
