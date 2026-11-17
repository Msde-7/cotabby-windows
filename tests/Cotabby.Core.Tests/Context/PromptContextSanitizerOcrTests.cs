using Cotabby.Core.Context;

namespace Cotabby.Core.Tests.Context;

/// <summary>
/// The bounded <see cref="PromptContextSanitizer.Sanitize(string, int)"/> and the stricter
/// <see cref="PromptContextSanitizer.SanitizeOcr"/>, which scores each token and drops lines that
/// are mostly OCR noise. Outputs are exact because whatever survives is copied into the prompt.
/// </summary>
public sealed class PromptContextSanitizerOcrTests
{
    /// <summary>The bound applies after normalizing, and a cut that lands on a space is trimmed.</summary>
    [Theory]
    [InlineData("abcdefghij", 5, "abcde")]
    [InlineData("hello", 5, "hello")]
    [InlineData("hello   world", 6, "hello")]
    public void Sanitize_BoundsToMaxCharacters(string input, int maxCharacters, string expected)
    {
        Assert.Equal(expected, PromptContextSanitizer.Sanitize(input, maxCharacters));
    }

    /// <summary>
    /// Standalone numbers, unlisted one- and two-letter tokens, repeated glyphs, letterless dotted
    /// numbers and lowercase-led mixed-case blobs are noise. The rest of the line survives when at
    /// least half its tokens do.
    /// </summary>
    [Theory]
    [InlineData("hello 50 world 424", "hello world")]
    [InlineData("I like if x", "I like if")]
    [InlineData("meeting notes aaaa", "meeting notes")]
    [InlineData("meeting notes 12.34", "meeting notes")]
    [InlineData("meeting notes abeW", "meeting notes")]
    [InlineData("meeting notes swift5 abc123", "meeting notes swift5")]
    public void SanitizeOcr_DropsNoiseTokensFromOtherwiseRealLines(string input, string expected)
    {
        Assert.Equal(expected, PromptContextSanitizer.SanitizeOcr(input));
    }

    /// <summary>Short technical words and uppercase acronyms are strong signal even without vowels.</summary>
    [Theory]
    [InlineData(
        "Cotabby CoHamster PR API context needs GeneralPaneView.swift normalizedBundleIdentifier jane@example.com",
        "Cotabby CoHamster PR API context needs GeneralPaneView.swift normalizedBundleIdentifier jane@example.com")]
    [InlineData("CPU ui ok", "CPU ui")]
    public void SanitizeOcr_KeepsTechnicalTokensAcronymsFilesAndEmails(string input, string expected)
    {
        Assert.Equal(expected, PromptContextSanitizer.SanitizeOcr(input));
    }

    /// <summary>
    /// A line is dropped whole when more than half its tokens are noise, or when every survivor is
    /// a weak short word, as in UI chrome like "we go to it".
    /// </summary>
    [Theory]
    [InlineData("50 x 99 hello")]
    [InlineData("50 424 102 99")]
    [InlineData("gLVWrt 54tbdbDX bDokE User")]
    [InlineData("we go to it")]
    [InlineData("*** ---")]
    [InlineData("")]
    public void SanitizeOcr_DropsNoiseDominatedAndWeakOnlyLines(string input)
    {
        Assert.Equal("", PromptContextSanitizer.SanitizeOcr(input));
    }

    [Fact]
    public void SanitizeOcr_DropsGarbageLineButKeepsRealLine()
    {
        const string input = "gLVWrt bDokE 54tbdbDX\nVisible task update Screen Recording copy for Cotabby";
        Assert.Equal("Visible task update Screen Recording copy for Cotabby", PromptContextSanitizer.SanitizeOcr(input));
    }

    /// <summary>
    /// CJK, Cyrillic and accented Latin carry real context but have no ASCII vowel and never match
    /// the English lists, so they must survive. That allowance must not let ASCII noise, or an
    /// unlisted short Latin word like "la", through on the same line.
    /// </summary>
    [Fact]
    public void SanitizeOcr_PreservesNonLatinScriptsWithoutAdmittingAsciiNoise()
    {
        const string input = "会議の議題を確認してください\nПривет команда смотрите задачу\nPréparez la réunion à Zürich";
        Assert.Equal(
            "会議の議題を確認してください\nПривет команда смотрите задачу\nPréparez réunion à Zürich",
            PromptContextSanitizer.SanitizeOcr(input));
        Assert.Equal("東京 オフィス", PromptContextSanitizer.SanitizeOcr("東京 gLVWrt オフィス 54tbdbDX"));
    }

    /// <summary>The OCR bound applies to the filtered, joined text, then trims a cut that ends on a space.</summary>
    [Fact]
    public void SanitizeOcr_BoundsToMaxCharacters()
    {
        Assert.Equal("alpha beta", PromptContextSanitizer.SanitizeOcr("alpha beta gamma delta epsilon", maxCharacters: 10));
        Assert.Equal("alpha beta", PromptContextSanitizer.SanitizeOcr("50 424\nalpha beta gamma", maxCharacters: 11));
    }
}
