using Cotabby.Core.Support;
using Cotabby.Core.Suggestions;
using Xunit;

namespace Cotabby.Core.Tests;

public class BaseCompletionPromptRendererTests
{
    [Fact]
    public void BarePrefix_NoPreface()
    {
        Assert.Equal("Hey John, I", BaseCompletionPromptRenderer.Prompt("Hey John, I"));
    }

    [Fact]
    public void TrailingWhitespaceTrimmed()
    {
        Assert.Equal("Hey John,", BaseCompletionPromptRenderer.Prompt("Hey John,  \n"));
    }

    [Fact]
    public void PersonaAndLanguage_PrecedePrefixWithBlankLine()
    {
        var prompt = BaseCompletionPromptRenderer.Prompt(
            "Hey John, I",
            userName: "Troy",
            languageInstruction: "The text is written in English.");
        Assert.Equal("Written by Troy.\nThe text is written in English.\n\nHey John, I", prompt);
    }

    [Fact]
    public void PrefixIsAlwaysTheFinalBytes()
    {
        var prompt = BaseCompletionPromptRenderer.Prompt("some text here", userName: "A");
        Assert.EndsWith("some text here", prompt);
    }

    [Fact]
    public void WindowPrefix_KeepsLast50Words()
    {
        var words = string.Join(' ', Enumerable.Range(0, 80).Select(i => $"w{i}"));
        var windowed = BaseCompletionPromptRenderer.WindowPrefix(words);
        Assert.StartsWith("w30 ", windowed);
        Assert.EndsWith("w79", windowed);
    }

    [Fact]
    public void WindowPrefix_CollapsesNewlinesToSpaces()
    {
        Assert.Equal("a b c", BaseCompletionPromptRenderer.WindowPrefix("a\nb\n\nc"));
    }

    [Fact]
    public void LanguageInstruction_FormatsOneOrMany()
    {
        Assert.Equal("The text is written in English.",
            BaseCompletionPromptRenderer.LanguageInstruction(["English"]));
        Assert.Equal("The text is written in English or German.",
            BaseCompletionPromptRenderer.LanguageInstruction(["English", "German"]));
        Assert.Null(BaseCompletionPromptRenderer.LanguageInstruction([]));
    }

    [Fact]
    public void Budget_PrefixSurvivesOversizedContext()
    {
        var prefix = new string('p', 2600);
        var prompt = BaseCompletionPromptRenderer.Prompt(prefix, userName: "Troy");
        // Prefix has top priority: it fills the whole budget and the persona
        // line is dropped rather than starving the caret text.
        Assert.DoesNotContain("Written by", prompt);
        Assert.Equal(2400, prompt.Length);
    }
}
