using Cotabby.Core.Support;

namespace Cotabby.Core.Tests;

public class TrailingDuplicationFilterTests
{
    private static bool Duplicates(string completion, string trailingText) =>
        TrailingDuplicationFilter.DuplicatesTrailingText(completion, trailingText);

    [Fact]
    public void ExactPrefixDuplicationIsDuplicate()
    {
        Assert.True(Duplicates("the dog", "the dog runs"));
    }

    [Fact]
    public void LeadingStrayGlyphStillMatchesAfterFolding()
    {
        Assert.True(Duplicates("**the dog", "the dog runs"));
    }

    [Fact]
    public void CaseInsensitiveDuplicationIsDuplicate()
    {
        Assert.True(Duplicates("The Dog", "the dog runs"));
    }

    [Fact]
    public void CompletionContainsWholeSuffixIsDuplicate()
    {
        Assert.True(Duplicates("ing the cat", "ing"));
    }

    [Fact]
    public void GenuineContinuationIsNotDuplicate()
    {
        Assert.False(Duplicates("world peace now", "domination plans"));
    }

    [Fact]
    public void EmptyTrailingTextIsNotDuplicate()
    {
        Assert.False(Duplicates("hello world", ""));
    }

    [Fact]
    public void ShortCompletionBelowOverlapFloorIsNotDuplicate()
    {
        Assert.False(Duplicates("ok", "okay then"));
    }

    [Fact]
    public void LongLeadingRunAtHalfOfCompletionIsDuplicate()
    {
        // Neither side is a prefix of the other, but the shared run "theywe" (6) reaches half of
        // the completion's folded length (12).
        Assert.True(Duplicates("they went home", "they were here"));
    }

    [Fact]
    public void ShortSharedLeadingRunBelowHalfIsNotDuplicate()
    {
        Assert.False(Duplicates("the dog barks loudly", "the cat"));
    }

    [Fact]
    public void JustBelowHalfOfCompletionIsNotDuplicate()
    {
        // The same six-character run, but the completion folds to 14 characters and needs 7.
        Assert.False(Duplicates("they went homely", "they were here"));
    }

    [Fact]
    public void CompletionContainingAShortSuffixIsNotDuplicate()
    {
        Assert.False(Duplicates("abcdef", "ab"));
    }

    [Fact]
    public void FloorAppliesToFoldedLengthNotRawLength()
    {
        // Only one alphanumeric survives folding.
        Assert.False(Duplicates("** - a!", "a b c d"));
        // A punctuation-only suffix folds to nothing.
        Assert.False(Duplicates("hello world", "... !!"));
    }

    [Fact]
    public void CjkDuplicationIsDetected()
    {
        Assert.True(Duplicates("良い天気", "良い天気ですね"));
    }
}
