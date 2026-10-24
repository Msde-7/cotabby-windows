using Cotabby.Core.History;

namespace Cotabby.Core.Tests.History;

public sealed class TypingHistoryIndexTests
{
    private static TypingHistoryRecord Record(string text, string app = "outlook", string? domain = null) => new()
    {
        Id = Guid.NewGuid(),
        BundleIdentifier = app,
        Domain = domain,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
        Text = text,
        Source = TypingHistorySource.Imported,
    };

    private static TypingHistoryQuery Query(
        string text, string app = "outlook", string? domain = null, string field = "") =>
        new(text, app, domain, field);

    [Fact]
    public void ReturnsThePassageThatSharesTheMostInformativeWords()
    {
        var index = new TypingHistoryIndex([
            Record("The Imperum POC for the SOC team starts Monday. We will connect Microsoft Sentinel first."),
            Record("Dinner tonight at eight? I can book the Italian place near the station if you like."),
            Record("Our quarterly report covers revenue, hiring and the office move planned for spring."),
        ]);

        var examples = index.Examples(Query("schedule the Imperum POC with Sentinel for the SOC"));

        Assert.Contains("Sentinel", examples[0]);
        Assert.DoesNotContain(examples, example => example.Contains("Dinner"));
    }

    [Fact]
    public void WeakOverlapReturnsNothing()
    {
        var index = new TypingHistoryIndex([
            Record("The Imperum POC for the SOC team starts Monday. We will connect Microsoft Sentinel first."),
        ]);

        Assert.Empty(index.Examples(Query("the team")));
    }

    [Fact]
    public void AnOlderSnapshotOfTheSameFieldIsSkipped()
    {
        const string draft = "Hi Arnaud, the Imperum POC with Sentinel is ready for the SOC team to review this week.";
        var index = new TypingHistoryIndex([Record(draft)]);

        Assert.Empty(index.Examples(Query("Imperum POC Sentinel SOC review", field: draft + " Let me")));
    }

    [Fact]
    public void VersionsOfTheDraftNeverCrowdOutAUsableExample()
    {
        // Seven earlier versions of the field's own text outrank the one usable example. They must
        // not fill every candidate slot and leave nothing to show.
        const string draft = "Status: the Imperum POC with Sentinel connectors is ready for SOC review this week and ";
        const string usable = "Separately, the Imperum connectors for Sentinel passed review yesterday.";
        var index = new TypingHistoryIndex(
            Enumerable.Range(0, 7).Select(i => Record(draft + $"version {i} ends here."))
                .Append(Record(usable, domain: "github.com")));
        var lookup = Query(draft, domain: "github.com", field: draft);

        Assert.Equal([usable], TypingHistoryIndex.Examples(index.Candidates(lookup), draft));
    }

    [Fact]
    public void PassageIsBoundedAndKeepsWholeSentences()
    {
        string text = "Short opener here. "
            + string.Concat(Enumerable.Repeat("Filler words about nothing in particular. ", 20))
            + "The Imperum POC uses Sentinel connectors for SOC alerts. "
            + string.Concat(Enumerable.Repeat("More filler text follows. ", 20));
        var index = new TypingHistoryIndex([Record(text)]);

        var passage = index.Examples(Query("Imperum POC Sentinel connectors SOC alerts"), maxCharacters: 120)
            .FirstOrDefault();

        Assert.NotNull(passage);
        Assert.True(passage.Length <= 120);
        Assert.Contains("The Imperum POC uses Sentinel connectors for SOC alerts.", passage);
    }

    [Fact]
    public void SameSiteWinsATie()
    {
        var index = new TypingHistoryIndex([
            Record("Pipeline status for the Imperum connector build is green again today.", "chrome", "github.com"),
            Record("Pipeline status for the Imperum connector build is green again today!", "chrome", "claude.ai"),
        ]);

        var examples = index.Examples(
            Query("Imperum connector pipeline build status", "chrome", "claude.ai"), limit: 1);

        Assert.Equal(["Pipeline status for the Imperum connector build is green again today!"], examples);
    }

    [Fact]
    public void StableTextOnlyChangesOnWholeWordBlocks()
    {
        Assert.Equal("", TypingHistoryQuery.StableText("one two three", wordsPerBlock: 4));
        Assert.Equal("a b c d", TypingHistoryQuery.StableText("a b c d e f", wordsPerBlock: 4));
        Assert.Equal(
            TypingHistoryQuery.StableText("a b c d e f", wordsPerBlock: 4),
            TypingHistoryQuery.StableText("a b c d e f g", wordsPerBlock: 4));
    }
}
