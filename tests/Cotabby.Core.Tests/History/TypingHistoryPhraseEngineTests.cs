using Cotabby.Core.Focus;
using Cotabby.Core.History;
using Cotabby.Core.Suggestions;
using Cotabby.Core.Support;
using Cotabby.Core.Tests.Suggestions;

namespace Cotabby.Core.Tests.History;

public sealed class TypingHistoryPhraseEngineTests
{
    private sealed class FixedHistory : ISuggestionHistoryProvider
    {
        public string? Phrase { get; set; }
        public List<bool> Engines { get; } = [];

        public IReadOnlyList<string> HistoryExamples(FocusedField field, bool usesEndpoint) => [];

        public string? PhraseContinuation(SuggestionRequest request, bool usesEndpoint)
        {
            Engines.Add(usesEndpoint);
            return Phrase;
        }
    }

    private static readonly SuggestionRequest Request =
        SuggestionRequestFactory.Build(Fields.Text("Best regards,"), "request", SuggestionSettings.Default);

    private static (TypingHistoryPhraseEngine Engine, ScriptedEngine Base, FixedHistory History) Make(
        string? phrase, bool usesEndpoint = false)
    {
        var inner = new ScriptedEngine { Completion = _ => " model" };
        var history = new FixedHistory { Phrase = phrase };
        return (new TypingHistoryPhraseEngine(inner, history, () => usesEndpoint), inner, history);
    }

    [Fact]
    public async Task ConfidentPhraseAnswersWithoutCallingTheModel()
    {
        var (engine, inner, _) = Make(" Senad");

        Assert.Equal(" Senad", await engine.GenerateAsync(Request, CancellationToken.None));
        Assert.True(engine.SpacingIsExact);
        Assert.Empty(inner.Requests);
    }

    [Fact]
    public async Task NoPhraseFallsThroughToTheModel()
    {
        var (engine, inner, _) = Make(null);

        Assert.Equal(" model", await engine.GenerateAsync(Request, CancellationToken.None));
        Assert.Single(inner.Requests);
    }

    [Fact]
    public async Task LiveEngineKindIsPassedToHistory()
    {
        var (engine, _, history) = Make(null, usesEndpoint: true);

        await engine.GenerateAsync(Request, CancellationToken.None);

        Assert.Equal([true], history.Engines);
    }
}

public sealed class TypingHistoryPromptTests
{
    [Fact]
    public void BasePromptQuotesExamplesBeforeTheCaretText()
    {
        string prompt = BaseCompletionPromptRenderer.Prompt(
            "The POC is", historyExamples: ["The Imperum POC starts Monday."]);

        Assert.Contains("Earlier writing by the same author:\n“The Imperum POC starts Monday.”", prompt);
        // The caret text must stay last.
        Assert.EndsWith("The POC is", prompt);
    }

    [Fact]
    public void HistorySitsAfterTheNotesAndBeforeTheClipboard()
    {
        string prompt = BaseCompletionPromptRenderer.Prompt(
            "The POC is", extendedContext: "Imperum is my company.", clipboardContext: "Sentinel connector list",
            historyExamples: ["The Imperum POC starts Monday."]);

        int notes = prompt.IndexOf("Notes the writer", StringComparison.Ordinal);
        int history = prompt.IndexOf("Earlier writing", StringComparison.Ordinal);
        int clipboard = prompt.IndexOf("On the clipboard", StringComparison.Ordinal);
        Assert.True(notes < history && history < clipboard, prompt);
    }

    [Fact]
    public void HistorySectionIsDroppedWholeRatherThanCutMidQuote()
    {
        string prompt = BaseCompletionPromptRenderer.Prompt(
            string.Concat(Enumerable.Repeat("word ", 30)),
            historyExamples: [string.Concat(Enumerable.Repeat("earlier ", 40))],
            contextBudget: 200);

        // A partly kept history section would leave an unclosed quote.
        Assert.DoesNotContain("“", prompt);
    }

    [Fact]
    public void ExamplesBeyondTheCapAreDroppedWhole()
    {
        string first = "First example. " + new string('a', 300);
        string second = "Second example. " + new string('b', 300);
        string third = "Third example. " + new string('c', 300);

        string prompt = BaseCompletionPromptRenderer.Prompt("The POC is", historyExamples: [first, second, third]);

        Assert.Contains(first, prompt);
        Assert.Contains(second, prompt);
        Assert.DoesNotContain("Third example", prompt);
    }

    [Fact]
    public void BasePromptWithoutExamplesIsUnchanged()
    {
        Assert.Equal(
            BaseCompletionPromptRenderer.Prompt("The POC is", extendedContext: "Notes"),
            BaseCompletionPromptRenderer.Prompt("The POC is", extendedContext: "Notes", historyExamples: []));
    }

    [Fact]
    public void FactoryDropsExamplesForTheEndpointEngine()
    {
        var field = Fields.Text("The POC is");
        string[] examples = ["The Imperum POC starts Monday."];

        var endpoint = SuggestionRequestFactory.Build(
            field, "request", SuggestionSettings.Default with { UsesEndpoint = true }, historyExamples: examples);
        var local = SuggestionRequestFactory.Build(field, "request", SuggestionSettings.Default, historyExamples: examples);

        Assert.Empty(endpoint.HistoryExamples);
        Assert.DoesNotContain("Imperum", CompletionPipeline.Prompt(endpoint, tokenBudget: null));
        Assert.Equal(examples, local.HistoryExamples);
        Assert.Contains("Imperum", CompletionPipeline.Prompt(local, tokenBudget: 2048));
    }

    [Fact]
    public void EndpointPromptNeverCarriesExamplesEvenWhenARequestDoes()
    {
        var request = SuggestionRequestFactory.Build(Fields.Text("The POC is"), "request", SuggestionSettings.Default)
            with { HistoryExamples = ["The Imperum POC starts Monday."] };

        Assert.DoesNotContain("Imperum", CompletionPipeline.Prompt(request, tokenBudget: null));
    }
}
