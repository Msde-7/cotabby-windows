using Cotabby.Core.Support.Output;
using Cotabby.Core.Support;

namespace Cotabby.Core.Suggestions;

/// <summary>
/// The prompt and cleanup steps every engine shares, so a request reads the same to
/// the local model and to an endpoint.
/// </summary>
public static class CompletionPipeline
{
    /// <summary>Context window the endpoint prompt is sized for. It shares the local model's prefix window.</summary>
    public const int EndpointContextWindowTokens = 4096;

    /// <param name="tokenBudget">Prompt token budget, already reduced by the request's output allowance.</param>
    /// <param name="forEndpoint">True when the prompt leaves the PC.</param>
    public static string Prompt(SuggestionRequest request, int tokenBudget, bool forEndpoint = false) =>
        BaseCompletionPromptRenderer.Prompt(
            request.Prefix,
            userName: request.UserName,
            // Text after the caret stays on the PC.
            trailingText: forEndpoint ? "" : request.Suffix,
            extendedContext: request.ExtendedContext,
            languageInstruction: request.LanguageInstruction,
            clipboardContext: request.ClipboardContext,
            surfaceLines: request.SurfaceLines,
            historyExamples: forEndpoint ? [] : request.HistoryExamples,
            screenContext: request.ScreenContext,
            maxScreenCharacters: forEndpoint ? 500 : 4000,
            screenPriority: forEndpoint ? 30 : 45,
            tokenBudget: tokenBudget);

    /// <summary>The endpoint prompt, budgeted like the local one so the whole output fits beside it.</summary>
    public static string EndpointPrompt(SuggestionRequest request) =>
        Prompt(
            request,
            PromptTokenBudget.ForRequest(PromptTokenBudget.Configured(EndpointContextWindowTokens), request.MaxTokens),
            forEndpoint: true);

    public static SuggestionNormalizationResult Normalize(string raw, string prompt, SuggestionRequest request)
    {
        var normalized = SuggestionTextNormalizer.NormalizeDetailed(
            raw,
            new SuggestionNormalizationRequest(
                Prompt: prompt,
                PrecedingText: request.Prefix,
                TrailingText: request.Suffix,
                IsMultiLineEnabled: !request.SingleLine,
                WordRange: request.WordRange));
        // Text that only continues the screen or the prompt's own notes is no suggestion, however clean it is.
        string shownContext = string.Join('\n', new[] { request.ScreenContext, request.LanguageInstruction }.Concat(request.SurfaceLines));
        return normalized with { Text = ContextEchoGuard.Trim(normalized.Text, shownContext, request.Prefix) };
    }
}
