using Cotabby.Core.Context;

namespace Cotabby.Core.Tests.Context;

/// <summary>
/// Each OCR guard in isolation, then <see cref="OcrTextHygiene.Clean"/> end to end. The
/// digit-substitution guard gets a keep/drop matrix because it must keep real technical tokens
/// (<c>utf8</c>, <c>RTX5070</c>, <c>20-core</c>) while dropping misreads (<c>qu81ity</c>, <c>h3llo</c>).
/// </summary>
public sealed class OcrTextHygieneTests
{
    private static OcrLine Line(string text, float confidence = 1f) => new(text, confidence);

    private static string[] Texts(IEnumerable<OcrLine> lines) => lines.Select(l => l.Text).ToArray();

    [Fact]
    public void DropLowConfidence_DropsBelowDefaultThreshold()
    {
        var input = new[] { Line("keep me", 0.41f), Line("drop me", 0.39f), Line("edge", 0.4f) };
        Assert.Equal(["keep me", "edge"], Texts(OcrTextHygiene.DropLowConfidence(input)));
    }

    [Fact]
    public void DropLowConfidence_HonorsCustomThreshold()
    {
        var input = new[] { Line("a", 0.7f), Line("b", 0.6f) };
        Assert.Equal(["a"], Texts(OcrTextHygiene.DropLowConfidence(input, threshold: 0.65f)));
    }

    [Fact]
    public void DropReplacementCharacter_DropsLinesWithReplacementGlyph()
    {
        var input = new[] { Line("clean line"), Line("corru�pted"), Line("also clean") };
        Assert.Equal(["clean line", "also clean"], Texts(OcrTextHygiene.DropReplacementCharacter(input)));
    }

    /// <summary>
    /// Density counts characters that are neither alphanumeric in any script, a space, nor common
    /// punctuation, and drops the line only when that share is strictly over 0.2.
    /// </summary>
    [Theory]
    [InlineData("Hello, world! This is fine.", true)]
    [InlineData("arr[i] = foo / bar; // ok", true)]
    [InlineData("gpt-4o-mini (v2.1)", true)]
    [InlineData("path/to/file.swift", true)]
    [InlineData("日本語のテキスト", true)]
    [InlineData("", true)]
    [InlineData("abcd→", true)]
    [InlineData("abc→", false)]
    [InlineData("┌───┐", false)]
    [InlineData("——••——", false)]
    public void DropHighSymbolDensity_KeepsTextAndDropsGlyphNoise(string text, bool kept)
    {
        Assert.Equal(!kept, OcrTextHygiene.DropHighSymbolDensity([Line(text)]).Count == 0);
    }

    [Fact]
    public void DropHighSymbolDensity_HonorsCustomThreshold()
    {
        // 1/4 symbols passes a 0.3 ceiling that the default 0.2 would reject.
        Assert.Equal(["abc→"], Texts(OcrTextHygiene.DropHighSymbolDensity([Line("abc→")], threshold: 0.3)));
    }

    [Theory]
    [InlineData("qu81ity")]
    [InlineData("h3llo")]
    [InlineData("x2y")]
    public void DropDigitSubstitution_DropsMisreadTokens(string token)
    {
        Assert.Empty(OcrTextHygiene.DropDigitSubstitution([Line(token)]));
    }

    [Theory]
    [InlineData("utf8")]
    [InlineData("v2")]
    [InlineData("3D")]
    [InlineData("5070")]
    [InlineData("20-core")]
    [InlineData("RTX5070")]
    [InlineData("N1X")]
    [InlineData("A1b")]
    public void DropDigitSubstitution_PreservesRealTokens(string token)
    {
        Assert.Equal([token], Texts(OcrTextHygiene.DropDigitSubstitution([Line(token)])));
    }

    [Fact]
    public void DropDigitSubstitution_DropsLineWhenAnyTokenMatches()
    {
        var input = new[] { Line("the qu81ity is poor"), Line("clean utf8 line") };
        Assert.Equal(["clean utf8 line"], Texts(OcrTextHygiene.DropDigitSubstitution(input)));
    }

    [Fact]
    public void DropDigitSubstitution_PreservesMixedRealNumbers()
    {
        var input = new[] { Line("use v2 on the 5070 with utf8 and 20-core") };
        Assert.Equal(Texts(input), Texts(OcrTextHygiene.DropDigitSubstitution(input)));
    }

    /// <summary>
    /// Keeps lines whose alphanumeric share of non-space characters is at least 0.5. Whitespace is
    /// left out of the denominator, so indentation cannot sink a wordy line.
    /// </summary>
    [Theory]
    [InlineData("This sentence has plenty of letters.", true)]
    [InlineData("        indented code here", true)]
    [InlineData("ab--", true)]
    [InlineData("ab---", false)]
    [InlineData("--- :: --- :: ---", false)]
    [InlineData("    ", false)]
    public void DropLowWordCharacterRatio_Matrix(string text, bool kept)
    {
        Assert.Equal(!kept, OcrTextHygiene.DropLowWordCharacterRatio([Line(text)]).Count == 0);
    }

    /// <summary>
    /// A line goes when its lowercased, whitespace-collapsed form is inside the equally normalized
    /// field text and is at least four characters long, so short coincidences like "to" stay.
    /// </summary>
    [Theory]
    [InlineData("hello world", "hello world", false)]
    [InlineData("Hello World", "hello world", false)]
    [InlineData("Hello    World", "the hello   world here", false)]
    [InlineData("read", "something to read", false)]
    [InlineData("rea", "something to read", true)]
    [InlineData("to", "this is something to read", true)]
    [InlineData("completely different", "hello world", true)]
    [InlineData("anything", "", true)]
    [InlineData("anything", "  \n ", true)]
    public void Strip_Matrix(string line, string field, bool kept)
    {
        Assert.Equal(!kept, OcrTextHygiene.Strip([Line(line)], field).Count == 0);
    }

    [Fact]
    public void Strip_HonorsCustomMinMatch()
    {
        Assert.Equal(["hello"], Texts(OcrTextHygiene.Strip([Line("hello")], "hello there", minMatch: 8)));
    }

    [Fact]
    public void Clean_RunsAllFiltersAndJoins()
    {
        OcrLine[] input =
        [
            Line("This is a genuine line of prose."),
            Line("low confidence noise", 0.1f),
            Line("corru�pted glyph"),
            Line("┌───┐"),
            Line("the qu81ity is poor"),
            Line("--- :: --- :: ---"),
            Line("echo of field"),
            Line("Another useful sentence with words."),
        ];

        Assert.Equal(
            ["This is a genuine line of prose.", "Another useful sentence with words."],
            OcrTextHygiene.Clean(input, "echo of field").Split('\n'));
    }

    [Fact]
    public void Clean_TrimsAndDropsEmptyLines()
    {
        Assert.Equal("spaced out", OcrTextHygiene.Clean([Line("   spaced out   "), Line("   ")], ""));
    }

    /// <summary>The line bound keeps the first lines in reading order. The default is 40.</summary>
    [Fact]
    public void Clean_BoundsMaxLinesKeepingEarliest()
    {
        var input = Enumerable.Range(0, 60).Select(i => Line($"line number {i} has words")).ToList();
        Assert.Equal("line number 0 has words\nline number 1 has words", OcrTextHygiene.Clean(input, "", maxLines: 2));
        Assert.Equal(40, OcrTextHygiene.Clean(input, "").Split('\n').Length);
        Assert.Equal("", OcrTextHygiene.Clean(input, "", maxLines: 0));
    }

    /// <summary>The character bound applies to the joined text, separators included.</summary>
    [Fact]
    public void Clean_BoundsMaxCharsOnJoinedText()
    {
        OcrLine[] input = [Line("first line"), Line("second line")];
        Assert.Equal("first line\nse", OcrTextHygiene.Clean(input, "", maxChars: 13));
        Assert.Equal("", OcrTextHygiene.Clean(input, "", maxChars: -1));
    }

    [Fact]
    public void Clean_WithNoSurvivingLines_ReturnsEmptyString()
    {
        Assert.Empty(OcrTextHygiene.Clean([Line("garbage", 0.1f), Line("┌─┐")], ""));
    }

    [Fact]
    public void Clean_PreservesTechnicalContent()
    {
        // Only shapes the guards promise to keep: trailing digits, versions and all-caps codes.
        // A token like "gpt-4o-mini" is left out because the digit guard cannot tell it from a misread.
        OcrLine[] input =
        [
            Line("func render(_ text: String) -> View {"),
            Line("config uses utf8 with v2.1"),
            Line("install on RTX5070 and N1X"),
        ];
        Assert.Equal(Texts(input), OcrTextHygiene.Clean(input, "").Split('\n'));
    }
}
