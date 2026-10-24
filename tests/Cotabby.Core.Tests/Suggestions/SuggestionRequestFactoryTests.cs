using Cotabby.Core.Focus;
using Cotabby.Core.Suggestions;
using Cotabby.Core.Support;

namespace Cotabby.Core.Tests.Suggestions;

public sealed class SuggestionRequestFactoryTests
{
    private const int MaxPrefixChars = 14000;
    private const int MaxSuffixChars = 192;

    private static readonly SuggestionSettings MultiLine = new()
    {
        WordRange = new SuggestionWordRange(7, 12),
        AllowMultiLine = true,
    };

    [Fact]
    public void Build_SplitsTheTextAtTheCaret()
    {
        var field = Fields.Text("hello world", process: "chrome") with { CaretOffset = 5 };

        var request = SuggestionRequestFactory.Build(field, "request-1", SuggestionSettings.Default);

        Assert.Equal("request-1", request.RequestId);
        Assert.Equal("hello", request.Prefix);
        Assert.Equal(" world", request.Suffix);
    }

    [Theory]
    [InlineData(-3, "", "hello")]
    [InlineData(99, "hello", "")]
    public void Build_ClampsACaretOutsideTheText(int caret, string prefix, string suffix)
    {
        var field = Fields.Text("hello") with { CaretOffset = caret };

        var request = SuggestionRequestFactory.Build(field, "request", SuggestionSettings.Default);

        Assert.Equal(prefix, request.Prefix);
        Assert.Equal(suffix, request.Suffix);
    }

    [Fact]
    public void Build_KeepsOnlyTheTextNearestTheCaret()
    {
        string before = "x" + new string('a', MaxPrefixChars);
        string after = new string('b', MaxSuffixChars) + "y";
        var field = Fields.Text(before + after) with { CaretOffset = before.Length };

        var request = SuggestionRequestFactory.Build(field, "request", SuggestionSettings.Default);

        Assert.Equal(before[1..], request.Prefix);
        Assert.Equal(after[..^1], request.Suffix);
    }

    [Theory]
    [InlineData(false, true, false, 32)]
    [InlineData(true, true, true, 16)]
    [InlineData(false, false, true, 16)]
    public void Build_IsMultiLineOnlyForAMultiLineFieldWithTheSettingOn(
        bool singleLineField, bool allowMultiLine, bool expectedSingleLine, int expectedMaxTokens)
    {
        var field = Fields.Text("hello", singleLine: singleLineField);

        var request = SuggestionRequestFactory.Build(field, "request", MultiLine with { AllowMultiLine = allowMultiLine });

        Assert.Equal(expectedSingleLine, request.SingleLine);
        Assert.Equal(expectedMaxTokens, request.MaxTokens);
    }

    [Fact]
    public void Build_CarriesTheUserNameAndLanguages()
    {
        var settings = new SuggestionSettings { UserName = "Ada", Languages = ["German"] };

        var request = SuggestionRequestFactory.Build(Fields.Text("hallo"), "request", settings);

        Assert.Equal("Ada", request.UserName);
        Assert.Contains("German", request.LanguageInstruction);
    }

    [Fact]
    public void Build_WithNoLanguages_HasAnEmptyLanguageInstruction()
    {
        var settings = new SuggestionSettings { Languages = [] };

        Assert.Equal("", SuggestionRequestFactory.Build(Fields.Text("hello"), "request", settings).LanguageInstruction);
    }

    private static readonly SuggestionSettings WithClipboard = new() { ClipboardContext = true };

    private static string Clipboard(string raw, SuggestionSettings? settings = null) =>
        SuggestionRequestFactory.Build(Fields.Text("Hello"), "request", settings ?? WithClipboard, raw).ClipboardContext;

    [Fact]
    public void Build_CarriesClipboardContextWhenEnabled()
    {
        var request = SuggestionRequestFactory.Build(
            Fields.Text("Hello"), "request", WithClipboard, "  Copied project notes.  ");

        Assert.Equal("Copied project notes.", request.ClipboardContext);
        Assert.Contains("On the clipboard: Copied project notes.", CompletionPipeline.EndpointPrompt(request));
    }

    [Fact]
    public void Build_SanitizesClipboardContext()
    {
        Assert.Equal(
            "jacob@example.com stage ready @ home",
            Clipboard("  `jacob@example.com` -- stage=ready +++ @ home!  "));
    }

    [Fact]
    public void Build_OmitsClipboardContextWhenDisabled()
    {
        Assert.Equal("", Clipboard("Copied project notes.", SuggestionSettings.Default));
        Assert.Equal("", SuggestionRequestFactory.Build(Fields.Text("Hello"), "request", WithClipboard).ClipboardContext);
    }

    [Fact]
    public void Build_ClipsClipboardContextAtItsCharacterCap()
    {
        string atCap = new('a', 1200);
        Assert.Equal(atCap, Clipboard(atCap));

        Assert.Equal(new string('a', 1197) + "...", Clipboard(new string('a', 1201)));

        // The cut lands just after the space, which is trimmed before the marker.
        Assert.Equal(new string('a', 1196) + "...", Clipboard(new string('a', 1196) + " " + new string('b', 10)));
    }

    [Theory]
    [InlineData("   \n\t ")]
    [InlineData("--- +++ ***")]
    [InlineData("@@ ... @")]
    public void Build_DropsClipboardContextWithoutAlphanumericSignal(string raw)
    {
        Assert.Equal("", Clipboard(raw));
    }

    [Fact]
    public void Build_DistillsALongClipboardAgainstThePrefix()
    {
        string copied = "Budget notes\nHello from the team\nUnrelated header\nAnother random line";

        Assert.Equal("Hello from the team", Clipboard(copied));
    }

    private static readonly FocusedField Email = Fields.Text("Thanks again for", process: "thunderbird") with
    {
        WindowTitle = "Re: Q3 budget - thunderbird",
    };

    [Fact]
    public void Build_IncludesSurfaceLines()
    {
        var request = SuggestionRequestFactory.Build(Email, "request", SuggestionSettings.Default);

        Assert.Equal(["Format: email; App: thunderbird; Title: Re: Q3 budget."], request.SurfaceLines);
        string prompt = CompletionPipeline.EndpointPrompt(request);
        Assert.Contains("Format: email; App: thunderbird; Title: Re: Q3 budget.", prompt);
        Assert.EndsWith("Thanks again for", prompt);
    }

    [Fact]
    public void Build_UsesTheHelpTextAsTheFieldPlaceholder()
    {
        var field = Fields.Text("Sounds good", process: "slack") with { HelpText = "Message #design" };

        var request = SuggestionRequestFactory.Build(field, "request", SuggestionSettings.Default);

        Assert.Equal(["Format: chat; App: slack; Field: Message #design."], request.SurfaceLines);
    }

    [Fact]
    public void Build_OmitsSurfaceLinesWhenDisabled()
    {
        var settings = new SuggestionSettings { SurfaceContext = false };

        Assert.Empty(SuggestionRequestFactory.Build(Email, "request", settings).SurfaceLines);
    }

    [Fact]
    public void Build_OmitsSurfaceLinesForCodeEditorsAndTerminals()
    {
        var editor = Fields.Text("// Returns the", process: "Code") with { WindowTitle = "Program.cs" };
        var integratedTerminal = editor with { ClassName = "xterm-helper-textarea" };
        var terminal = Fields.Text("git sta", process: "WindowsTerminal") with { WindowTitle = "pwsh" };

        foreach (var field in new[] { editor, integratedTerminal, terminal })
        {
            Assert.Empty(SuggestionRequestFactory.Build(field, "request", SuggestionSettings.Default).SurfaceLines);
        }
    }

    [Fact]
    public void Prefix_KeepsSeparatorsWhenTheWordBudgetDropsOldText()
    {
        string retained = string.Concat(Enumerable.Repeat("w\n\t", 2399)) + "last  \n";

        Assert.Equal(retained, SuggestionRequestFactory.Prefix("discard this " + retained));
    }

    [Fact]
    public void Prefix_CharacterWindowKeepsUnicodeAndTrailingWhitespace()
    {
        string text = string.Concat(Enumerable.Repeat("👩🏽‍💻", 2600)) + "\n\tHello  ";

        string prefix = SuggestionRequestFactory.Prefix(text, maxCharacters: 2000);

        Assert.Equal(string.Concat(Enumerable.Repeat("👩🏽‍💻", 1991)) + "\n\tHello  ", prefix);
    }

    [Fact]
    public void Prefix_NonPositiveBudgetsProduceAnEmptyPrefix()
    {
        Assert.Equal("", SuggestionRequestFactory.Prefix("Hello world", maxCharacters: 0));
        Assert.Equal("", SuggestionRequestFactory.Prefix("Hello world", maxWords: 0));
    }

    [Theory]
    [InlineData("\n  one two three", "\n  one two three")]
    [InlineData("zero\n\none two three ", "one two three ")]
    public void Prefix_WordBudgetBoundary(string text, string expected)
    {
        Assert.Equal(expected, SuggestionRequestFactory.Prefix(text, maxWords: 3));
    }

    [Fact]
    public void Prefix_CharacterWindowMayStartMidWord()
    {
        Assert.Equal("ef ghi", SuggestionRequestFactory.Prefix("abcdef ghi", maxCharacters: 6));
    }

    [Fact]
    public void TheWindowKeepsLineAndParagraphBreaks()
    {
        const string text = "Hi Sam,\n\nThanks for the update.\nBest";

        Assert.Equal(text, SuggestionRequestFactory.Prefix(text));
    }

    [Fact]
    public void EndpointPrompt_LeavesRoomForTheRequestsWholeOutput()
    {
        string longText = string.Concat(Enumerable.Repeat("The quarterly numbers look strong again. ", 600));
        var settings = new SuggestionSettings { WordRange = new SuggestionWordRange(30, 50) };
        var request = SuggestionRequestFactory.Build(Fields.Text(longText), "request", settings);

        string prompt = CompletionPipeline.EndpointPrompt(request);

        int budget = PromptTokenBudget.ForRequest(PromptTokenBudget.Configured(4096), request.MaxTokens);
        Assert.True(TokenCountEstimator.Estimate(prompt) <= budget);
        Assert.EndsWith("strong again. ", prompt);
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("   \t  ", false)]
    [InlineData("\n\n", false)]
    [InlineData(" \n\t \n  ", false)]
    [InlineData("a", true)]
    [InlineData("word", true)]
    [InlineData("Hello, wor", true)]
    [InlineData("  hello", true)]
    [InlineData("hello  ", true)]
    [InlineData("今日は", true)]
    public void ShouldGenerate_NeedsNonWhitespaceButNotATrailingDelimiter(string text, bool expected) =>
        Assert.Equal(expected, SuggestionRequestFactory.ShouldGenerate(text));

    [Theory]
    [InlineData("w", false)]
    [InlineData("word", false)]
    [InlineData("Please schedu", false)]
    [InlineData("I don't", false)]
    [InlineData("Try (wor", false)]
    [InlineData("  ", false)]
    [InlineData("word ", true)]
    [InlineData("word,", true)]
    [InlineData("word.", true)]
    [InlineData("word\n", true)]
    [InlineData("今日は", true)]
    [InlineData("user_name", true)]
    [InlineData("version v2", true)]
    public void ShouldGenerate_WaitingForWordsStillServesUnspacedLanguages(string text, bool expected) =>
        Assert.Equal(expected, SuggestionRequestFactory.ShouldGenerate(text, suggestWithinWords: false));

    [Fact]
    public void ShouldGenerate_DeclinesACaretInsideAToken()
    {
        Assert.False(SuggestionRequestFactory.ShouldGenerate("head", "phones"));
        Assert.False(SuggestionRequestFactory.ShouldGenerate("jane", "@example.com"));
        Assert.True(SuggestionRequestFactory.ShouldGenerate("Thanks", ". Bye"));
        Assert.True(SuggestionRequestFactory.ShouldGenerate("Thanks for", ""));
    }
}
