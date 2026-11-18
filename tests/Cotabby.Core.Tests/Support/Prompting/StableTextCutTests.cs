using System.Text;
using Cotabby.Core.Focus;
using Cotabby.Core.Suggestions;
using Cotabby.Core.Support;
using Cotabby.Core.Tests.Suggestions;

namespace Cotabby.Core.Tests;

/// <summary>
/// The model reuses the prompt it evaluated last up to the first changed character. These tests
/// type into long documents a character at a time and check that the prompt's head holds still.
/// </summary>
public class StableTextCutTests
{
    private static readonly string[] Vocabulary =
        ("the of and to in is that for it as was with be by on not this are or from at which but have they you were " +
         "their one all we can her has there been if more when will would who so about project meeting update team " +
         "review schedule budget design release customer report quarter numbers please thanks follow next week plan " +
         "draft feedback change notes summary").Split(' ');

    /// <summary>Varied English-like prose with sentences and the odd paragraph break.</summary>
    internal static string Prose(int characters, int seed)
    {
        var random = new Random(seed);
        var text = new StringBuilder();
        int words = 0;
        while (text.Length < characters)
        {
            string word = Vocabulary[random.Next(Vocabulary.Length)];
            text.Append(words == 0 ? char.ToUpperInvariant(word[0]) + word[1..] : word);
            words++;
            if (words > 8 + random.Next(10))
            {
                text.Append(random.Next(6) == 0 ? ".\n\n" : ". ");
                words = 0;
            }
            else
            {
                text.Append(' ');
            }
        }
        return text.ToString(0, characters);
    }

    /// <summary>The last 14,000 characters before the caret, as the focus tracker hands over a long document.</summary>
    private static string TrackerWindow(string before) => before.Length <= 14000 ? before : before[^14000..];

    private static int SharedHead(string a, string b)
    {
        int i = 0;
        while (i < a.Length && i < b.Length && a[i] == b[i]) i++;
        return i;
    }

    [Fact]
    public void WindowStartIsAWordStartNoEarlierThanAskedAndWithinReach()
    {
        string text = Prose(6000, 3);
        for (int start = 1; start < 5000; start += 37)
        {
            int anchored = StableTextCut.WindowStart(text, start, maxAdvance: 1000);
            Assert.InRange(anchored, start, start + 1000);
            if (anchored > start)
            {
                Assert.True(char.IsLetterOrDigit(text[anchored]));
                Assert.False(char.IsLetterOrDigit(text[anchored - 1]));
            }
        }
    }

    [Fact]
    public void WindowStartDependsOnlyOnTheTextAfterIt()
    {
        string text = Prose(6000, 4);
        int anchored = StableTextCut.WindowStart(text, 1000, maxAdvance: 2048);
        Assert.True(anchored > 1000);

        // The same text with its first 700 characters gone, as when a longer document's tail slides.
        int shifted = StableTextCut.WindowStart(text[700..], 1000 - 700, maxAdvance: 2048);

        Assert.Equal(anchored - 700, shifted);
    }

    [Fact]
    public void WindowStartKeepsTheStartWithoutRoomToMove()
    {
        Assert.Equal(3, StableTextCut.WindowStart("one two three four five six seven", 3, maxAdvance: 0));
        Assert.Equal(4, StableTextCut.WindowStart("one two three", 4, maxAdvance: 2048));
    }

    [Theory]
    [InlineData(12500)]
    [InlineData(30000)]
    public void RequestPrefixStartHoldsStillWhileTheWriterTypes(int documentLength)
    {
        const int keys = 500;
        string document = Prose(documentLength + keys, 21);
        int moves = 0;
        int? previousStart = null;
        for (int caret = documentLength; caret < documentLength + keys; caret++)
        {
            string prefix = SuggestionRequestFactory.Prefix(TrackerWindow(document[..caret]));
            Assert.EndsWith(prefix, document[..caret]);
            int start = caret - prefix.Length;
            if (previousStart is { } previous && start != previous) moves++;
            previousStart = start;
            // The 2,400-word budget holds about 13,000 characters of this prose, and the anchor
            // gives up at most 2,048 of them.
            Assert.InRange(prefix.Length, 10000, 14000);
        }

        // An exact cut moved with every word or character typed.
        Assert.InRange(moves, 0, keys / 50);
    }

    [Theory]
    [InlineData(1500, false)]
    [InlineData(12500, true)]
    [InlineData(30000, true)]
    public void PromptHeadHoldsStillWhileTheWriterTypes(int documentLength, bool withScreen)
    {
        const int keys = 500;
        string document = Prose(documentLength + keys, 22);
        string screen = Prose(3800, 7).Replace(". ", ".\n");
        var settings = SuggestionSettings.Default with { ScreenContext = true };
        string? previous = null;
        long reevaluated = 0;
        int resets = 0;
        for (int caret = documentLength; caret < documentLength + keys; caret++)
        {
            string before = TrackerWindow(document[..caret]);
            var field = Fields.Text(before);
            var request = SuggestionRequestFactory.Build(field, "r", settings, screenContext: withScreen ? screen : null);
            int budget = PromptTokenBudget.ForRequest(PromptTokenBudget.Configured(4096), request.MaxTokens);
            string prompt = CompletionPipeline.Prompt(request, budget);
            Assert.True(TokenCountEstimator.Estimate(prompt) <= budget);
            Assert.EndsWith(before[^40..], prompt);
            if (previous is not null)
            {
                int shared = SharedHead(previous, prompt);
                reevaluated += prompt.Length - shared;
                if (shared < prompt.Length / 2) resets++;
            }
            previous = prompt;
        }

        // Typing one character changes a few characters at the end. A cut that moves on every
        // keystroke made a quarter of keystrokes evaluate more than half the prompt again.
        Assert.InRange(resets, 0, keys / 50);
        Assert.InRange(reevaluated / (keys - 1), 0, 400);
    }

    [Fact]
    public void LiveSectionCutByTheBudgetStartsAtAnAnchorAndEndsTheFill()
    {
        string live = Prose(3000, 5);
        var sections = new[]
        {
            new PromptSection("notes", "Some notes", Priority: 10, MinChars: 0, MaxChars: 100, SectionTruncation.PreserveStart),
            new PromptSection("prefix", live, 100, 1, live.Length, SectionTruncation.PreserveEnd, PreservesWhitespace: true, LiveText: true),
        };

        var kept = PromptSectionBudget.Allocate(sections, totalChars: 2000);

        var only = Assert.Single(kept);
        Assert.EndsWith(only.Content, live);
        Assert.InRange(only.Content.Length, 2000 - 500, 1999);
        int start = live.Length - only.Content.Length;
        Assert.Equal(StableTextCut.WindowStart(live, live.Length - 2000, maxAdvance: 500), start);
    }

    [Fact]
    public void BudgetAfterALiveSectionMovesOnlyInSteps()
    {
        string notes = Prose(1000, 6);
        var cuts = new HashSet<string>();
        for (int typed = 0; typed < 200; typed++)
        {
            string live = new('x', 500 + typed);
            var kept = PromptSectionBudget.Allocate(
                [
                    new PromptSection("notes", notes, 10, 0, 1000, SectionTruncation.PreserveStart),
                    new PromptSection("prefix", live, 100, 1, live.Length, SectionTruncation.PreserveEnd, LiveText: true),
                ],
                totalChars: 1280);
            cuts.Add(kept[0].Content);
        }

        // 200 characters typed against a step of 1280 / 64 = 20 characters.
        Assert.InRange(cuts.Count, 1, 11);
    }

    [Fact]
    public void SectionsWithoutLiveTextKeepTheExactCut()
    {
        var kept = PromptSectionBudget.Allocate(
            [
                new PromptSection("prefix", "aaaa bbbb", 100, 1, 100, SectionTruncation.PreserveEnd, PreservesWhitespace: true),
                new PromptSection("notes", "cccccccc", 10, 0, 100, SectionTruncation.PreserveStart),
            ],
            totalChars: 13);
        Assert.Equal(["aaaa bbbb", "cccc"], kept.Select(s => s.Content));
    }
}
