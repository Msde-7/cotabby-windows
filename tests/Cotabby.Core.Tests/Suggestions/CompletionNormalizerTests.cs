using Cotabby.Core.Suggestions;
using Cotabby.Core.Support;
using Cotabby.Core.Support.Output;

namespace Cotabby.Core.Tests.Suggestions;

public sealed class CompletionNormalizerTests
{
    private static readonly string Screen = string.Join('\n',
        "Quarterly budget review with Priya on Thursday at the office",
        "Do you want to save changes to the draft",
        "Ln 12 Col 4",
        "the numbers look strong again this quarter and the team agreed");

    private static IEnumerable<string> Streams(string prefix)
    {
        string[] words = prefix.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        yield return " and the team will follow up next week with the draft.";
        yield return " " + string.Join(' ', words[^6..]) + " and then some more words here";
        yield return " Do you want to save changes to the draft before closing";
        yield return " soon.\nLn 3 Col 9\nDon't save";
        yield return "Text before the caret: please send the report";
        yield return "\n\nthe numbers look strong again this quarter and the team agreed to it";
        yield return " <b>bold</b> again again again again";
        yield return " 会議は明日の午後三時から第二会議室で行います";
        yield return " " + string.Join(' ', words[^4..]).ToUpperInvariant() + " twice";
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void OneNormalizerPerRequestMatchesNormalizingEachPartialAfresh(bool singleLine, bool withScreen)
    {
        string prefix = StableTextCutTests.Prose(9000, 31)
            + " the numbers look strong again this quarter and the team agreed. Then we ";
        var request = new SuggestionRequest
        {
            RequestId = "r",
            Prefix = prefix,
            Suffix = " later",
            SingleLine = singleLine,
            MaxTokens = 32,
            LanguageInstruction = "Write in English.",
            ScreenContext = withScreen ? Screen : "",
            SurfaceLines = ["Format: notes; App: Notepad."],
            WordRange = new SuggestionWordRange(3, 12),
        };
        string prompt = CompletionPipeline.Prompt(request, 4000);
        var normalizer = new CompletionNormalizer(prompt, request);
        string shown = string.Join('\n', new[] { request.ScreenContext, request.LanguageInstruction }.Concat(request.SurfaceLines));

        int compared = 0;
        int echoCuts = 0;
        foreach (string stream in Streams(prefix).Append(prompt + " and so on"))
        {
            // A stream that echoes the prompt is checked from just before the echo ends.
            int first = stream.StartsWith(prompt, StringComparison.Ordinal) ? prompt.Length - 3 : 1;
            for (int length = first; length <= stream.Length; length++)
            {
                string raw = stream[..length];
                var reused = normalizer.Normalize(raw);
                Assert.Equal(CompletionPipeline.Normalize(raw, prompt, request), reused);

                // The same steps through the per-call entry points the shared context replaces.
                var separate = SuggestionTextNormalizer.NormalizeDetailed(raw, new SuggestionNormalizationRequest(
                    prompt, prefix, request.Suffix, !singleLine, request.WordRange));
                Assert.Equal(separate with { Text = ContextEchoGuard.Trim(separate.Text, shown, prefix) }, reused);
                if (reused.Text != separate.Text) echoCuts++;
                compared++;
            }
        }
        Assert.True(compared > 400);
        // The screen rules ran on some partials, not only the text cleanup.
        Assert.Equal(withScreen, echoCuts > 0);
    }
}
