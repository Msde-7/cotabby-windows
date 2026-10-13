using System.Runtime.CompilerServices;
using Cotabby.Core.Support;
using Cotabby.Core.Suggestions;
using Microsoft.Extensions.Logging;

namespace Cotabby.Inference;

/// <summary>
/// LLamaSharp-backed <see cref="ISuggestionEngine"/>. It renders a base-model conditioning prompt
/// (preface plus the exact caret prefix), runs one constrained decode on the persistent runtime
/// core, normalizes the result once and surfaces it as a single final chunk.
/// </summary>
/// <remarks>
/// The whole normalized suggestion is presented at once, because prompt echo, chat leaks and
/// trailing duplicates are only detectable on the full string.
/// </remarks>
public sealed class LlamaSuggestionEngine : ISuggestionEngine
{
    private readonly LlamaRuntimeManager _runtime;
    private readonly ILogger<LlamaSuggestionEngine> _logger;

    public LlamaSuggestionEngine(LlamaRuntimeManager runtime, ILogger<LlamaSuggestionEngine> logger)
    {
        _runtime = runtime;
        _logger = logger;
    }

    public bool IsReady => _runtime.IsReady;

    public async IAsyncEnumerable<SuggestionChunk> GenerateAsync(
        SuggestionRequest request,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var core = _runtime.Core;
        if (core is null)
        {
            _logger.LogWarning("Generate called while runtime not ready (req={ReqId}).", request.RequestId);
            yield return new SuggestionChunk(string.Empty, IsFinal: true);
            yield break;
        }

        var options = new LlamaGenerationOptions
        {
            MaxTokens = request.MaxTokens,
            SingleLine = request.SingleLine,
            ForceWordContinuation =
                MidWordContinuationPolicy.ShouldForceContinuation(request.Prefix, request.Suffix),
        };

        // Fill-in-the-middle is used only when real text follows on the same line and the model
        // exposes infill markers. Everything else goes through the base continuation prompt.
        bool useFim = core.SupportsFillInMiddle && HasMeaningfulSuffix(request.Suffix);

        string prompt;
        Task<string> generation;
        if (useFim)
        {
            prompt = request.Prefix;
            int maxPromptTokens = Math.Max(1, core.ContextWindowTokens - request.MaxTokens);
            generation = core.GenerateAsync(
                core.BuildFimTokens(request.Prefix, request.Suffix, maxPromptTokens), options, ct);
        }
        else
        {
            // The whole output allowance of the request has to fit beside the prompt.
            prompt = CompletionPipeline.Prompt(
                request,
                PromptTokenBudget.ForRequest(
                    PromptTokenBudget.Configured(core.ContextWindowTokens), request.MaxTokens));
            if (prompt.Length == 0)
            {
                yield return new SuggestionChunk(string.Empty, IsFinal: true);
                yield break;
            }
            generation = core.GenerateAsync(prompt, options, ct);
        }

        string raw;
        try
        {
            raw = await generation.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            yield break;
        }

        if (ct.IsCancellationRequested) yield break;

        var normalized = CompletionPipeline.Normalize(raw, prompt, request);

        if (normalized.Suppression is { } suppression)
        {
            if (suppression != CompletionSuppressionReason.EmptyGeneration)
            {
                _logger.LogInformation("Req {ReqId}: suppressed ({Reason}).", request.RequestId, suppression);
                _logger.LogDebug("Req {ReqId}: raw=\"{Raw}\".", request.RequestId, LogText.Escape(raw));
            }
            yield return new SuggestionChunk(string.Empty, IsFinal: true);
            yield break;
        }

        yield return new SuggestionChunk(normalized.Text, IsFinal: false);
        yield return new SuggestionChunk(string.Empty, IsFinal: true);
    }

    private static bool HasMeaningfulSuffix(string suffix)
    {
        foreach (char c in suffix)
        {
            if (c is '\n' or '\r') return false;
            if (!char.IsWhiteSpace(c)) return true;
        }
        return false;
    }
}
