using Cotabby.Core.Support;

namespace Cotabby.Core.Tests;

/// <summary>
/// Only one usable inline continuation reaches the overlay, and an empty result names the stage that
/// emptied it.
/// </summary>
public class SuggestionTextNormalizerTests
{
    /// <summary>The prefix defaults to the preceding text, as the request factory builds it.</summary>
    private static SuggestionNormalizationRequest Request(
        string precedingText = "Hello", string? prefixText = null, string prompt = "PROMPT",
        string trailingText = "", bool isMultiLineEnabled = false) =>
        new(prompt, prefixText ?? precedingText, precedingText, trailingText, isMultiLineEnabled);

    private static string Normalize(string raw, SuggestionNormalizationRequest request) =>
        SuggestionTextNormalizer.NormalizeDetailed(raw, request).Text;

    [Fact]
    public void RemovesChatTemplateMarkersAndPromptEcho()
    {
        Assert.Equal(
            " useful continuation",
            Normalize(
                "PROMPT_PAYLOAD<|im_start|> useful continuation<|im_end|>", Request(prompt: "PROMPT_PAYLOAD")));
    }

    [Fact]
    public void TruncatesAtStopMarkerSalvagingPrefix()
    {
        // The model answers, then hallucinates a new chat turn.
        Assert.Equal(
            "be there soon.",
            Normalize("be there soon.<|im_end|><|im_start|>user\nAnything else?", Request("I will ")));
    }

    [Theory]
    [InlineData("Hello world", "Hello world, with a small addition", ", with a small addition")]
    [InlineData("I love 👩🏽\u200D💻", "I love 👩🏽\u200D💻 coding", " coding")]
    [InlineData("今日は", "今日は良い天気", "良い天気")]
    // A full prompt echo is peeled first, then the prefix echo underneath.
    [InlineData("Hello", "PROMPT\nHello there", " there")]
    public void RemovesVisiblePrefixEcho(string precedingText, string raw, string expected)
    {
        Assert.Equal(expected, Normalize(raw, Request(precedingText)));
    }

    [Theory]
    [InlineData("\n\nnext words only\nsecond paragraph should be dropped", "next words only")]
    [InlineData("next\r\nsecond", "next")]
    public void SingleLineKeepsOnlyTheFirstContentLine(string raw, string expected)
    {
        Assert.Equal(expected, Normalize(raw, Request()));
    }

    [Fact]
    public void TrimsEdgeControlCharactersButRejectsInteriorOnes()
    {
        Assert.Equal("foo", Normalize("\tfoo", Request("x")));
        Assert.Equal(
            new SuggestionNormalizationResult("", CompletionSuppressionReason.UnsafeToInsert),
            SuggestionTextNormalizer.NormalizeDetailed("foo\tbar", Request("x")));
    }

    [Theory]
    [InlineData("Hello ", null, " world", "world")]
    [InlineData("Hello\t", null, " world", "world")]
    [InlineData("Hello", null, " world", " world")]
    [InlineData("hello world", null, "world is great", " is great")]
    [InlineData("hello world ", null, "world is great", "is great")]
    // "i like" overlaps two words back, and the comparison ignores case.
    [InlineData("hi i like", null, "I like matcha in the morning", " matcha in the morning")]
    // Both a two-word and a four-word alignment match. The longest wins. The prefix is set apart so
    // the verbatim prefix-echo stage cannot handle this first.
    [InlineData("the cat the cat", "Hello", "the cat the cat sat", " sat")]
    public void SeamWhitespaceAndEchoSuppression(string precedingText, string? prefixText, string raw, string expected)
    {
        Assert.Equal(expected, Normalize(raw, Request(precedingText, prefixText)));
    }

    [Theory]
    [InlineData("I am ", "Text before caret: going to the store", "going to the store")]
    [InlineData("send the ", "App: report by Friday", "report by Friday")]
    [InlineData("The ", "Task:\nText before caret:\nquick brown fox", "quick brown fox")]
    [InlineData("The ", "continuation: quick fox", "quick fox")]
    // A colon in real text, or a label later in the continuation, is user content.
    [InlineData("my list ", "TODO: buy milk", "TODO: buy milk")]
    [InlineData("finish the ", "first Task: review", "first Task: review")]
    public void ScaffoldingLabels(string precedingText, string raw, string expected)
    {
        Assert.Equal(expected, Normalize(raw, Request(precedingText)));
    }

    [Theory]
    [InlineData("I look forward to hearing", " from you", " from you")]
    [InlineData("Please send the sched", "ule", "ule")]
    [InlineData("I look forward to hearing ", " from you", "from you")]
    [InlineData(
        "I look forward to hearing", " from you\nabout the schedule  \t\n\nextra paragraph",
        " from you\nabout the schedule")]
    [InlineData("I look forward to hearing", " from you  \n\t", " from you")]
    [InlineData("Notes", "first line\nsecond line\n\nrunaway paragraph", "first line\nsecond line")]
    [InlineData("Notes", "first line\nsecond line", "first line\nsecond line")]
    [InlineData("Notes", "\n\nfirst line\nsecond line", "first line\nsecond line")]
    // Carriage returns are removed up front. Left in place they would trip the safety gate.
    [InlineData("Notes", "one\r\ntwo", "one\ntwo")]
    public void MultiLine(string precedingText, string raw, string expected)
    {
        Assert.Equal(expected, Normalize(raw, Request(precedingText, isMultiLineEnabled: true)));
    }

    [Theory]
    [InlineData(" f")]
    [InlineData(" from")]
    [InlineData(" from you")]
    [InlineData(" from you.\nBest wishes")]
    public void MultiLineCumulativePartialsKeepTheInsertionBoundary(string partial)
    {
        Assert.Equal(partial, Normalize(partial, Request("I look forward to hearing", isMultiLineEnabled: true)));
    }

    [Theory]
    [InlineData("<think>the user is mid-sentence</think>next words", "next words")]
    [InlineData("<think>line one\nline two</think>\nnext words", "next words")]
    [InlineData("<think>first</think>real<think>second never closes", "real")]
    public void StripsThinkBlocks(string raw, string expected)
    {
        Assert.Equal(expected, Normalize(raw, Request()));
    }

    [Theory]
    [InlineData("I love ", "", "this product", "this product", null)]
    [InlineData("x", "", "", "", CompletionSuppressionReason.EmptyGeneration)]
    [InlineData("x", "", "   \n  ", "", CompletionSuppressionReason.EmptyGeneration)]
    [InlineData("x", "", "<|im_start|><|im_end|>", "", CompletionSuppressionReason.NormalizedToEmpty)]
    [InlineData("Hello", "", "<think>reasoning that never closes", "", CompletionSuppressionReason.NormalizedToEmpty)]
    [InlineData("x", "", "Continuation:", "", CompletionSuppressionReason.NormalizedToEmpty)]
    [InlineData("x", "", "PROMPT", "", CompletionSuppressionReason.NormalizedToEmpty)]
    [InlineData(
        "Hello", " existing suffix", " existing suffix and extra generated text", "",
        CompletionSuppressionReason.DuplicatesTrailingText)]
    [InlineData("hello world", "", "world", "", CompletionSuppressionReason.EchoesPrecedingText)]
    [InlineData("x", "", "abc\uFFFD", "", CompletionSuppressionReason.UnsafeToInsert)]
    public void AttributesEverySuppression(
        string precedingText, string trailingText, string raw, string expectedText,
        CompletionSuppressionReason? expectedSuppression)
    {
        Assert.Equal(
            new SuggestionNormalizationResult(expectedText, expectedSuppression),
            SuggestionTextNormalizer.NormalizeDetailed(raw, Request(precedingText, trailingText: trailingText)));
    }

    [Fact]
    public void HoldsSingleLineSuggestionsToTheWordRange()
    {
        var request = Request("I ") with { WordRange = new SuggestionWordRange(2, 4) };
        Assert.Equal("have been thinking about", Normalize("have been thinking about this for a while", request));
        // Multi-line completions are not trimmed.
        Assert.Equal(
            "have been thinking about this", Normalize("have been thinking about this", request with { IsMultiLineEnabled = true }));
    }
}
