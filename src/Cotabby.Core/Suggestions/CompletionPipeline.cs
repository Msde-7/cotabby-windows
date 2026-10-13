using Cotabby.Core.Support;

namespace Cotabby.Core.Suggestions;

/// <summary>
/// The prompt and cleanup steps every engine shares, so a request reads the same to
/// the local model and to an endpoint.
/// </summary>
public static class CompletionPipeline
{
    /// <summary>Character budget for a prompt that leaves the PC.</summary>
    private const int EndpointContextBudget = 2400;

    /// <param name="tokenBudget">Prompt token budget of the local model, or null for an endpoint.</param>
    public static string Prompt(SuggestionRequest request, int? tokenBudget)
    {
        bool forEndpoint = tokenBudget is null;
        return BaseCompletionPromptRenderer.Prompt(
            request.Prefix,
            userName: request.UserName,
            // Text after the caret stays on the PC.
            trailingText: forEndpoint ? "" : request.Suffix,
            extendedContext: request.ExtendedContext,
            languageInstruction: request.LanguageInstruction,
            clipboardContext: request.ClipboardContext,
            surfaceLines: request.SurfaceLines,
            contextBudget: forEndpoint ? EndpointContextBudget : BaseCompletionPromptRenderer.DefaultContextBudget,
            tokenBudget: tokenBudget);
    }

    public static SuggestionNormalizationResult Normalize(string raw, string prompt, SuggestionRequest request) =>
        SuggestionTextNormalizer.NormalizeDetailed(
            raw,
            new SuggestionNormalizationRequest(
                Prompt: prompt,
                PrefixText: request.Prefix,
                PrecedingText: request.Prefix,
                TrailingText: request.Suffix,
                IsMultiLineEnabled: !request.SingleLine,
                WordRange: request.WordRange));
}
