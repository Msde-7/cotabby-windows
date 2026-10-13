using Cotabby.Core.Support;

namespace Cotabby.Core.Tests;

public class SuggestionLengthPolicyTests
{
    [Fact]
    public void TextWithinTheWindowIsUntouched()
    {
        Assert.Equal(" for your help.", SuggestionLengthPolicy.Trimmed(" for your help.", minimum: 4, maximum: 7));
        Assert.Equal(
            " one two three four five six seven",
            SuggestionLengthPolicy.Trimmed(" one two three four five six seven", minimum: 4, maximum: 7));
    }

    [Fact]
    public void TextPastTheMaximumIsCutAtAWordBoundary()
    {
        Assert.Equal(
            " I've been thinking about this for a",
            SuggestionLengthPolicy.Trimmed(" I've been thinking about this for a while now", minimum: 4, maximum: 7));
    }

    [Fact]
    public void AClauseBoundaryInsideTheWindowIsPreferred()
    {
        Assert.Equal(
            " and I will send it later today,",
            SuggestionLengthPolicy.Trimmed(
                " and I will send it later today, once the numbers are in", minimum: 4, maximum: 7));
    }

    [Fact]
    public void AClauseBoundaryBeforeTheMinimumIsIgnored()
    {
        Assert.Equal(
            " yes, and the rest of the report",
            SuggestionLengthPolicy.Trimmed(" yes, and the rest of the report is ready now", minimum: 4, maximum: 7));
    }

    [Fact]
    public void LeadingWhitespaceSurvives()
    {
        Assert.Equal(" a b c d", SuggestionLengthPolicy.Trimmed(" a b c d e f g h", minimum: 2, maximum: 4));
    }
}
