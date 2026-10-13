using Cotabby.Core.Support;

namespace Cotabby.Core.Tests;

public class InsertionSafetyGateTests
{
    [Theory]
    [InlineData("hello there")]
    // Closing a bracket or ending a sentence is a legitimate inline completion.
    [InlineData(")")]
    [InlineData(".")]
    [InlineData("first line\nsecond line")]
    [InlineData("  indented")]
    [InlineData("🎉 café")]
    public void LegitimateCompletionsAreSafe(string completion)
    {
        Assert.True(InsertionSafetyGate.IsSafeToInsert(completion));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\n")]
    [InlineData("\u00A0")]
    [InlineData("ab\uFFFDcd")]
    [InlineData("a\tb")]
    [InlineData("a\u001Bb")]
    [InlineData("a\u007Fb")]
    // Only the line feed is exempt, so a carriage return is refused.
    [InlineData("a\rb")]
    public void JunkCompletionsAreUnsafe(string completion)
    {
        Assert.False(InsertionSafetyGate.IsSafeToInsert(completion));
    }
}
