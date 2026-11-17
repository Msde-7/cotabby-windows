using Cotabby.Core.Context;
using Cotabby.Core.Focus;

namespace Cotabby.Core.Tests.Context;

/// <summary>
/// The cleaning half of the screen-context pipeline: hygiene and selection for each privacy
/// profile, the character bounds, the meaningful-signal gate and the window-title fallback.
/// Capture, permissions and the pixel cache live in the Windows reader and are not covered here.
/// </summary>
public sealed class ScreenContextExcerptTests
{
    private const string MeaningfulLine =
        "GeneralPaneView.swift should say Screen Recording is required for autocomplete context";

    private static readonly FocusedField Field = new()
    {
        ProcessId = 100,
        ProcessName = "devenv",
        Text = "Screen Recording",
        CaretOffset = "Screen Recording".Length,
        CaretRect = new ScreenRect(140, 420, 2, 18),
        FieldRect = new ScreenRect(100, 380, 600, 120),
        IsSingleLine = false,
        IsSecure = false,
    };

    private static VisualContextConfiguration Configuration(int maxSummaryCharacters) =>
        new(700, 1600, 12, 500, maxSummaryCharacters, CapturesEntireWindow: false);

    /// <summary>One OCR line per text line, confident enough that only the other guards apply.</summary>
    private static ScreenCapture Capture(string text, string? windowTitle = null) =>
        new(text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => new OcrLine(l, 0.9f)).ToList(), null, windowTitle);

    private static string Excerpt(ScreenCapture capture, VisualContextConfiguration? configuration = null) =>
        ScreenContextSource.Excerpt(capture, Field, configuration ?? VisualContextConfiguration.Endpoint);

    [Fact]
    public void LocalProfileReadsTheWholeWindowAndEndpointProfileStaysBounded()
    {
        var lines = Enumerable.Range(0, 80).Select(i => new OcrLine($"Project agenda item {i}", 1f)).ToList();
        var capture = new ScreenCapture(lines, null, null);

        string endpoint = Excerpt(capture, VisualContextConfiguration.Endpoint);
        string local = Excerpt(capture, VisualContextConfiguration.Local);

        Assert.True(endpoint.Length <= 1500);
        Assert.DoesNotContain("item 79", endpoint);
        Assert.Contains("item 79", local);
        Assert.True(local.Length <= 4000);
    }

    [Fact]
    public void OcrTextIsCappedAndSanitized()
    {
        var configuration = Configuration(maxSummaryCharacters: 60);

        string excerpt = Excerpt(Capture("gLVWrt bDokE 54tbdbDX\n" + MeaningfulLine), configuration);

        Assert.True(excerpt.Length <= configuration.MaxSummaryCharacters);
        Assert.DoesNotContain("gLVWrt", excerpt);
        Assert.DoesNotContain("54tbdbDX", excerpt);
        Assert.Contains("GeneralPaneView.swift", excerpt);
    }

    [Fact]
    public void AllNoiseOcrIsUnavailable()
    {
        Assert.Equal("", Excerpt(Capture("gLVWrt bDokE 54tbdbDX\n50 424 102 99")));
    }

    [Fact]
    public void DropsLowConfidenceOcrLines()
    {
        // A clean sentence at low confidence must go even though no other guard would catch it,
        // which shows the recognizer's per-line confidence reaches the hygiene pass.
        var capture = new ScreenCapture(
            [
                new OcrLine("The quarterly report is due on Friday afternoon.", 0.2f),
                new OcrLine("Please review the attached budget spreadsheet carefully.", 0.95f),
            ],
            null,
            null);

        string excerpt = Excerpt(capture, Configuration(maxSummaryCharacters: 200));

        Assert.DoesNotContain("quarterly report", excerpt);
        Assert.Contains("budget spreadsheet", excerpt);
    }

    [Fact]
    public void NoRecognizedTextFallsBackToTheWindowTitle()
    {
        // An image-heavy window can read as nothing while its title still names the document.
        var capture = new ScreenCapture([], null, "Quarterly budget review draft for the finance meeting");

        Assert.Contains("Quarterly budget review", Excerpt(capture));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("x1 9z")]
    public void NoRecognizedTextWithoutAUsableTitleIsUnavailable(string? windowTitle)
    {
        // A missing title and one that is only chrome noise both give up rather than promote junk.
        Assert.Equal("", Excerpt(new ScreenCapture([], null, windowTitle)));
    }
}
