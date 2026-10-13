using Cotabby.Core.Support;

namespace Cotabby.Core.Tests;

public class ConfidenceSuppressionPolicyTests
{
    [Fact]
    public void DisabledFloorNeverSuppresses()
    {
        Assert.False(ConfidenceSuppressionPolicy.ShouldSuppress(-50.0, double.NegativeInfinity));
    }

    [Fact]
    public void BelowFloorSuppresses()
    {
        Assert.True(ConfidenceSuppressionPolicy.ShouldSuppress(-3.0, -2.0));
    }

    [Fact]
    public void AboveFloorDoesNotSuppress()
    {
        Assert.False(ConfidenceSuppressionPolicy.ShouldSuppress(-1.0, -2.0));
    }

    [Fact]
    public void AtFloorDoesNotSuppress()
    {
        Assert.False(ConfidenceSuppressionPolicy.ShouldSuppress(-2.0, -2.0));
    }

    [Fact]
    public void NonFiniteLogprobs()
    {
        // Negative infinity (a zero-probability token) is maximally unsure.
        Assert.True(ConfidenceSuppressionPolicy.ShouldSuppress(double.NegativeInfinity, -2.0));
        // NaN compares false against everything, so neither a NaN average nor a NaN floor suppresses.
        Assert.False(ConfidenceSuppressionPolicy.ShouldSuppress(double.NaN, -2.0));
        Assert.False(ConfidenceSuppressionPolicy.ShouldSuppress(-50.0, double.NaN));
    }
}
