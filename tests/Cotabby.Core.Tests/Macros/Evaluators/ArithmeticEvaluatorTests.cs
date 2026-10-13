using Cotabby.Core.Macros;

namespace Cotabby.Core.Tests.Macros;

public class ArithmeticEvaluatorTests
{
    private readonly ArithmeticEvaluator _sut = new();

    [Fact]
    public void AdditionInsertsResultOnly()
    {
        var result = _sut.Evaluate("5+5=");
        Assert.Equal("= 10", result?.PreviewText);
        Assert.Equal("10", result?.InsertionText);
    }

    [Fact]
    public void WithoutTrailingEqualsInsertsResultOnly()
    {
        Assert.Equal("10", _sut.Evaluate("5+5")?.InsertionText);
    }

    [Fact]
    public void MultiplyWithXInsertsResultOnly()
    {
        Assert.Equal("25", _sut.Evaluate("5x5")?.InsertionText);
    }

    [Fact]
    public void PowerIsRightAssociative()
    {
        // 2^(3^2) = 512. A left-associative parser would produce (2^3)^2 = 64.
        Assert.Equal("= 512", _sut.Evaluate("2^3^2")?.PreviewText);
    }

    [Fact]
    public void MultiplicationBindsTighterThanAddition()
    {
        Assert.Equal("14", _sut.Evaluate("2+3*4")?.InsertionText);
        Assert.Equal("3", _sut.Evaluate("10-4-3")?.InsertionText);
    }

    [Fact]
    public void UnicodeAndUppercaseOperatorAliases()
    {
        Assert.Equal("12", _sut.Evaluate("3×4")?.InsertionText);
        Assert.Equal("12", _sut.Evaluate("3X4")?.InsertionText);
        Assert.Equal("2.5", _sut.Evaluate("10÷4")?.InsertionText);
    }

    [Fact]
    public void UnaryMinusInsideAnExpressionIsAllowed()
    {
        // Only a lone signed number is rejected. Once a binary operator appears it is an expression.
        Assert.Equal("= -2", _sut.Evaluate("-5+3")?.PreviewText);
        Assert.Equal("-6", _sut.Evaluate("2*-3")?.InsertionText);
    }

    [Fact]
    public void WhitespaceIsIgnored()
    {
        Assert.Equal("20", _sut.Evaluate("( 2 + 3 ) * 4")?.InsertionText);
    }

    [Fact]
    public void PercentAloneCountsAsAnOperator()
    {
        Assert.Equal("0.5", _sut.Evaluate("50%")?.InsertionText);
    }

    [Fact]
    public void FloatingPointNoiseIsTrimmedBySignificantDigits()
    {
        Assert.Equal("0.3", _sut.Evaluate("0.1+0.2")?.InsertionText);
    }

    [Fact]
    public void Parentheses()
    {
        Assert.Equal("= 20", _sut.Evaluate("(2+3)*4")?.PreviewText);
    }

    [Fact]
    public void DivisionRoundsToSignificantDigits()
    {
        Assert.Equal("= 3.333333333", _sut.Evaluate("10/3")?.PreviewText);
    }

    [Fact]
    public void TrailingPercentMeansPercent()
    {
        Assert.Equal("= 30", _sut.Evaluate("200*15%")?.PreviewText);
    }

    [Fact]
    public void BareNumberIsNotAMacro()
    {
        Assert.Null(_sut.Evaluate("5"));
    }

    [Fact]
    public void UnarySignedNumberIsNotAMacro()
    {
        Assert.Null(_sut.Evaluate("-5"));
    }

    [Fact]
    public void DivisionByZeroReturnsNull()
    {
        Assert.Null(_sut.Evaluate("5/0"));
    }

    [Fact]
    public void IncompleteExpressionReturnsNull()
    {
        Assert.Null(_sut.Evaluate("5+"));
    }

    [Fact]
    public void UnbalancedParenthesesReturnNull()
    {
        Assert.Null(_sut.Evaluate("(2+3"));
        Assert.Null(_sut.Evaluate("2+3)"));
    }

    [Theory]
    [InlineData("abc+1")]
    [InlineData("1e5+1")]
    [InlineData("1.2.3+1")]
    [InlineData("=")]
    [InlineData("5++")]
    public void NonNumericTokensReturnNull(string query)
    {
        Assert.Null(_sut.Evaluate(query));
    }

    [Fact]
    public void OverflowToInfinityReturnsNull()
    {
        Assert.Null(_sut.Evaluate("10^400"));
    }

    [Fact]
    public void FormatDropsTheDecimalPointForWholeValuesBelowTheSafeRange()
    {
        Assert.Equal("3", ArithmeticEvaluator.Format(3));
        Assert.Equal("-2.5", ArithmeticEvaluator.Format(-2.5));
        // At 1e15 and above the integer path is skipped, so huge values fall back to exponent notation.
        Assert.Equal("1e+15", ArithmeticEvaluator.Format(1e15));
        Assert.Equal("1.5e-05", ArithmeticEvaluator.Format(0.000015));
    }

    [Fact]
    public void FormatRejectsNonFiniteValues()
    {
        Assert.Null(ArithmeticEvaluator.Format(double.PositiveInfinity));
        Assert.Null(ArithmeticEvaluator.Format(double.NaN));
    }

    [Theory]
    [InlineData("de-DE")]
    [InlineData("fr-FR")]
    public void ParsingAndFormattingIgnoreTheCurrentCulture(string culture)
    {
        // These cultures use a decimal comma. Input and output must still use a point.
        using var scope = new CultureScope(culture);
        Assert.Equal("0.3", _sut.Evaluate("0.1+0.2")?.InsertionText);
        Assert.Equal("3.75", _sut.Evaluate("1.5*2.5")?.InsertionText);
        Assert.Equal("1234567.5", _sut.Evaluate("1234567+0.5")?.InsertionText);
        Assert.Equal("-2", _sut.Evaluate("-5+3")?.InsertionText);
        Assert.Null(_sut.Evaluate("1,5+1"));
    }
}
