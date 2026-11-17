using Cotabby.Win32.Insertion;

namespace Cotabby.Inference.Tests;

public class ForegroundTargetTests
{
    private static bool Never() => throw new InvalidOperationException("The window check should not run.");

    [Fact]
    public void ForegroundProcessOfTheField_Passes()
    {
        Assert.True(ForegroundTarget.Accepts(42, 42, Never));
    }

    [Fact]
    public void AnotherProcessInFront_PassesOnlyWhenItsWindowHoldsTheField()
    {
        Assert.True(ForegroundTarget.Accepts(42, 7, () => true));
        Assert.False(ForegroundTarget.Accepts(42, 7, () => false));
    }

    [Fact]
    public void TargetWithoutAProcess_CannotBeCheckedAndPasses()
    {
        Assert.True(ForegroundTarget.Accepts(0, 7, Never));
    }
}
