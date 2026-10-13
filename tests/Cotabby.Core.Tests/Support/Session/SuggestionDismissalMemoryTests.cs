using Cotabby.Core.Support;

namespace Cotabby.Core.Tests;

/// <summary>
/// A dismissed first word stays suppressed while the user types through it in the same field. The
/// memory is scoped to the field and the trailing text, bounded in time, and capped.
/// </summary>
public class SuggestionDismissalMemoryTests
{
    private static bool Hidden(
        SuggestionDismissalMemory memory, string prefix, string completion, ulong field = 1, double time = 1) =>
        memory.Suppresses(field, prefix, "", completion, time);

    [Fact]
    public void DismissalSurvivesTypingWithinTheWordButExpiresAndStaysFieldScoped()
    {
        var memory = new SuggestionDismissalMemory();
        memory.Record(1, "Please sche", "", "dule a meeting", 0);
        Assert.True(Hidden(memory, "Please sche", "dule another meeting"));
        Assert.True(Hidden(memory, "Please sched", "ule"));
        Assert.False(Hidden(memory, "Please sche", "matic"));
        Assert.False(Hidden(memory, "Please sche", "dule", field: 2));
        // The 15 second lifetime is exclusive at its end.
        Assert.False(Hidden(memory, "Please sche", "dule", time: 15));
        Assert.False(Hidden(memory, "Please schedule ", "another meeting"));
    }

    [Fact]
    public void OnlyTheFirstWordOfALeadingSpaceCompletionIsRemembered()
    {
        var memory = new SuggestionDismissalMemory();
        memory.Record(1, "Please schedule", "", " another meeting", 0);
        Assert.True(Hidden(memory, "Please schedule", " another day"));
        // Typing into the dismissed word, and a proposal that is a prefix of it.
        Assert.True(Hidden(memory, "Please schedule an", "other"));
        Assert.True(Hidden(memory, "Please schedule", " an"));
        // A different first word is a fresh offer.
        Assert.False(Hidden(memory, "Please schedule", " anyway"));
    }

    [Fact]
    public void TrailingTextChangeAndEmptyCompletionsDoNotSuppress()
    {
        var memory = new SuggestionDismissalMemory();
        memory.Record(1, "Hi ", "", "", 0);
        Assert.False(memory.Suppresses(1, "Hi ", "", "", 1));

        memory.Record(1, "Hi ", " end", "there", 0);
        Assert.True(memory.Suppresses(1, "Hi ", " end", "there", 1));
        Assert.False(memory.Suppresses(1, "Hi ", " edited", "there", 1));
    }

    [Fact]
    public void MemoryKeepsOnlyTheEightMostRecentDismissals()
    {
        var memory = new SuggestionDismissalMemory();
        for (ulong key = 1; key <= 9; key++)
        {
            memory.Record(key, "Please sche", "", "dule", 0);
        }
        Assert.False(Hidden(memory, "Please sche", "dule", field: 1));
        for (ulong key = 2; key <= 9; key++)
        {
            Assert.True(Hidden(memory, "Please sche", "dule", field: key));
        }
    }

    [Fact]
    public void LongFieldsAnchorOnTheBoundedPrefixSuffix()
    {
        // Only the last 256 characters are stored, and they are located in the live prefix.
        string longPrefix = string.Concat(Enumerable.Repeat("lorem ipsum ", 40)) + "Please sche";
        var memory = new SuggestionDismissalMemory();
        memory.Record(1, longPrefix, "", "dule it", 0);
        Assert.True(Hidden(memory, longPrefix, "dule"));
        Assert.True(Hidden(memory, longPrefix + "du", "le"));
    }
}
