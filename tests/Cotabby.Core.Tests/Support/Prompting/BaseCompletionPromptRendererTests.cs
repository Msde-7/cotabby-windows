using Cotabby.Core.Support;

namespace Cotabby.Core.Tests;

/// <summary>
/// No instruction preamble, the exact caret prefix is always the final bytes, and supplied context
/// stays inside its budget. Outputs are asserted as whole strings because the base model continues
/// whatever it is given.
/// </summary>
public class BaseCompletionPromptRendererTests
{
    [Theory]
    [InlineData("I am writing to ")]
    [InlineData("doing my aft")]
    [InlineData("see you   \n")]
    [InlineData("Hi team,\n\n\t- ")]
    public void NoContextReturnsExactPrefixVerbatim(string prefix)
    {
        Assert.Equal(prefix, BaseCompletionPromptRenderer.Prompt(prefix));
    }

    [Fact]
    public void WhitespaceOnlyOptionalInputsAddNoPreface()
    {
        string prompt = BaseCompletionPromptRenderer.Prompt(
            "A new thought ",
            userName: "  \n",
            trailingText: "  \n\t",
            extendedContext: " ",
            languageInstruction: "\n",
            clipboardContext: "\t");
        Assert.Equal("A new thought ", prompt);
    }

    [Fact]
    public void AllContextRendersEverySectionInOrderWithPrefixLast()
    {
        // The writer's name is absent because the caret is mid-sentence, not after a sign-off.
        string prompt = BaseCompletionPromptRenderer.Prompt(
            "the meeting is at ",
            userName: " Jacob ",
            trailingText: " on Friday.",
            extendedContext: "Project Matcha ships in June.",
            languageInstruction: "Write in English.",
            clipboardContext: "zoom link",
            surfaceLines: ["Format: email; App: Mail; Title: Re: Q3."]);
        Assert.Equal(
            string.Join('\n',
                "Format: email; App: Mail; Title: Re: Q3.",
                "Write in English.",
                "Notes the writer keeps in mind: Project Matcha ships in June.",
                "On the clipboard: zoom link",
                "Later in the same passage:\n“ on Friday.”") + "\n\nthe meeting is at ",
            prompt);
    }

    [Fact]
    public void FollowingTextIsBoundedQuotedContextBeforeExactPrefix()
    {
        const string prefix = "We are meeting ";
        string prompt = BaseCompletionPromptRenderer.Prompt(prefix, trailingText: new string('a', 300));
        Assert.Equal("Later in the same passage:\n“" + new string('a', 192) + "”\n\n" + prefix, prompt);
    }

    [Fact]
    public void SurfaceLinesJoinIntoOneSectionAndEmptyLinesAddNone()
    {
        Assert.Equal(
            "Format: email; Title: Re: Q3. Field: Message.\n\nThanks",
            BaseCompletionPromptRenderer.Prompt(
                "Thanks", surfaceLines: ["Format: email; Title: Re: Q3.", "Field: Message."]));
        Assert.Equal("Thanks", BaseCompletionPromptRenderer.Prompt("Thanks", surfaceLines: []));
    }

    [Fact]
    public void CharacterBudgetDropsLowerPrioritySectionsWhenExhausted()
    {
        string prompt = BaseCompletionPromptRenderer.Prompt(
            "abc",
            languageInstruction: "Write in English.",
            clipboardContext: "zoom link",
            contextBudget: 3 + "Write in English.".Length);
        Assert.Equal("Write in English.\n\nabc", prompt);
    }

    [Fact]
    public void PrefixLongerThanBudgetKeepsCaretSideAndDropsContext()
    {
        Assert.Equal("world", BaseCompletionPromptRenderer.Prompt("hello world", userName: "Jacob", contextBudget: 5));
    }

    [Fact]
    public void FollowingTextDropsAsAWholeWhenOnlyThePrefixFits()
    {
        const string prefix = "We are meeting ";
        Assert.Equal(
            prefix,
            BaseCompletionPromptRenderer.Prompt(prefix, trailingText: " on Friday.", contextBudget: prefix.Length + 10));
    }

    [Fact]
    public void ZeroBudgetDoesNotRestoreAnUnboundedPrefix()
    {
        Assert.Equal("", BaseCompletionPromptRenderer.Prompt("This must not escape the budget.", contextBudget: 0));
    }

    [Fact]
    public void NotesSectionAdmitsTheFullAdvertisedExtendedContext()
    {
        // 1200 characters is the settings limit for extended context.
        string notes = new('n', 1200);
        Assert.Equal(
            $"Notes the writer keeps in mind: {notes}\n\nabc",
            BaseCompletionPromptRenderer.Prompt("abc", extendedContext: notes));
    }

    [Theory]
    [InlineData(24)]
    [InlineData(240)]
    public void SurfaceSectionStaysBoundedWithoutChangingCaretWhitespace(int availableSurfaceCharacters)
    {
        const string prefix = "Hi team,\n\nNext step:\n\t- ";
        string surface = "Format: browser; App: Google Chrome; Domain: " + new string('d', 210)
            + "; Title: " + new string('t', 80) + ".";
        string prompt = BaseCompletionPromptRenderer.Prompt(
            prefix, surfaceLines: [surface], contextBudget: prefix.Length + availableSurfaceCharacters);
        Assert.EndsWith("\n\n" + prefix, prompt);
        // Allocation counts content. The renderer adds its two newline separators.
        Assert.Equal(prefix.Length + availableSurfaceCharacters + 2, prompt.Length);
    }

    [Theory]
    [InlineData(1, "abcd")]
    [InlineData(5, "Write in English.\n\nabcd")]
    public void TokenBudgetFillsPrefixFirstThenContext(int tokenBudget, string expected)
    {
        // "abcd" is one estimated token and "Write in English." is four.
        string prompt = BaseCompletionPromptRenderer.Prompt(
            "abcd",
            languageInstruction: "Write in English.",
            clipboardContext: "zoom link",
            tokenBudget: tokenBudget);
        Assert.Equal(expected, prompt);
    }

    [Fact]
    public void TokenBudgetPreservesParagraphBreaksAndCaretIndentation()
    {
        const string prefix = "Hi team,\n\nAgenda:\n\t- ";
        Assert.Equal(
            "Write in English.\n\n" + prefix,
            BaseCompletionPromptRenderer.Prompt(prefix, languageInstruction: "Write in English.", tokenBudget: 100));
    }

    [Fact]
    public void TokenBudgetAdmitsAPrefixLargerThanTheOldCharacterBudget()
    {
        string prefix = string.Concat(Enumerable.Repeat("every word counts here ", 109)) + "and the end";
        Assert.True(prefix.Length > 2400);
        string prompt = BaseCompletionPromptRenderer.Prompt(
            prefix,
            languageInstruction: "Write in English.",
            tokenBudget: PromptTokenBudget.Configured(contextWindowTokens: 4096));
        Assert.Equal("Write in English.\n\n" + prefix, prompt);
    }

    [Fact]
    public void LanguageConditionsWithoutNamingTheWriterAtAnOpening()
    {
        string prompt = BaseCompletionPromptRenderer.Prompt(
            "Hi team,",
            userName: "Jacob",
            languageInstruction: "Write in English.");
        Assert.DoesNotContain("Jacob", prompt);
        Assert.Contains("Write in English.", prompt);
        Assert.EndsWith("Hi team,", prompt);
    }

    [Fact]
    public void WriterIsNamedOnlyWhereTheCaretFollowsAValediction()
    {
        string signing = BaseCompletionPromptRenderer.Prompt(
            "Could you add the budget numbers before Friday?\n\nThanks again,\n", userName: "Jacob");
        Assert.Contains("Written by Jacob.", signing);
        Assert.EndsWith("Thanks again,\n", signing);

        foreach (string prefix in new[] { "", "Hi", "Thanks for", "I will forward the draft to", "the rest of the" })
        {
            Assert.DoesNotContain("Jacob", BaseCompletionPromptRenderer.Prompt(prefix, userName: "Jacob"));
        }
    }

    [Theory]
    [InlineData(962, 50, 962)]
    [InlineData(962, 120, 892)]
    [InlineData(30, 120, 0)]
    public void RequestTokenBudgetGivesUpTheOutputBeyondTheCeiling(int configured, int maxTokens, int expected)
    {
        Assert.Equal(expected, PromptTokenBudget.ForRequest(configured, maxTokens));
    }
}
