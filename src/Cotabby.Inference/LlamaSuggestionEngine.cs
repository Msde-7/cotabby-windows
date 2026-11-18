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
/// trailing duplicates are only detectable on the full string. Partial results only let the
/// coordinator keep a request alive while the user types the words it is producing.
/// </remarks>
public sealed class LlamaSuggestionEngine : ISuggestionEngine
{
    private readonly LlamaRuntimeManager _runtime;
    private readonly ILogger<LlamaSuggestionEngine> _logger;
    private readonly Lock _prewarmLock = new();

    // Guarded by _prewarmLock. Removed from here before its owner disposes it, so a cancel made
    // under the lock never reaches a disposed source.
    private CancellationTokenSource? _prewarm;

    public LlamaSuggestionEngine(LlamaRuntimeManager runtime, ILogger<LlamaSuggestionEngine> logger)
    {
        _runtime = runtime;
        _logger = logger;
    }

    public bool IsReady => _runtime.IsReady;

    public bool SpacingIsExact => true;

    /// <summary>
    /// Evaluates the prompt <paramref name="request"/> would generate from, without sampling, so
    /// the first real request in a field only evaluates what was typed since. The prompt is
    /// rendered exactly as <see cref="GenerateAsync"/> renders it.
    /// </summary>
    /// <remarks>
    /// Best effort: failures are logged and swallowed, because a missed prewarm costs nothing the
    /// cold path would not pay anyway. A newer prewarm or any generation cancels the one in
    /// flight, so a real request never waits behind a prewarm for longer than one short chunk.
    /// </remarks>
    public async Task PrewarmAsync(SuggestionRequest request, CancellationToken ct)
    {
        var core = _runtime.Core;
        if (core is null) return;
        string prompt = RenderPrompt(request, core);
        if (prompt.Length == 0) return;

        using var prewarm = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lock (_prewarmLock)
        {
            _prewarm?.Cancel();
            _prewarm = prewarm;
        }
        try
        {
            await core.PrewarmAsync(prompt, OptionsFor(request), prewarm.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
            // The model was unloaded meanwhile.
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Req {ReqId}: prewarm skipped.", request.RequestId);
        }
        finally
        {
            lock (_prewarmLock)
            {
                if (_prewarm == prewarm) _prewarm = null;
            }
        }
    }

    public async Task<string> GenerateAsync(
        SuggestionRequest request, CancellationToken ct, Action<string>? onPartial = null)
    {
        // A prewarm still running must not make this request wait behind it for the gate.
        CancelPrewarm();

        var core = _runtime.Core;
        if (core is null)
        {
            _logger.LogWarning("Generate called while runtime not ready (req={ReqId}).", request.RequestId);
            return string.Empty;
        }

        var options = OptionsFor(request);
        string prompt = RenderPrompt(request, core);
        if (prompt.Length == 0) return string.Empty;
        if (onPartial is not null)
        {
            options = options with
            {
                OnPartial = raw =>
                {
                    string text = CompletionPipeline.Normalize(raw, prompt, request).Text;
                    if (text.Length > 0) onPartial(text);
                },
            };
        }

        string raw;
        try
        {
            // A prewarm that started while the prompt was rendered could otherwise take the free
            // gate first and evaluate a cold prompt with nothing left to cancel it. Nothing awaits
            // between here and the gate.
            CancelPrewarm();
            raw = await core.GenerateAsync(prompt, options, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return string.Empty;
        }
        catch (ObjectDisposedException)
        {
            // The model was unloaded or switched after this request picked up its core.
            _logger.LogDebug("Req {ReqId}: the model was unloaded before generation.", request.RequestId);
            return string.Empty;
        }

        if (ct.IsCancellationRequested) return string.Empty;

        var normalized = CompletionPipeline.Normalize(raw, prompt, request);

        if (normalized.Suppression is { } suppression && suppression != CompletionSuppressionReason.EmptyGeneration)
        {
            _logger.LogInformation("Req {ReqId}: suppressed ({Reason}).", request.RequestId, suppression);
            _logger.LogDebug("Req {ReqId}: raw=\"{Raw}\".", request.RequestId, LogText.Escape(raw));
        }
        return normalized.Text;
    }

    private void CancelPrewarm()
    {
        lock (_prewarmLock)
        {
            _prewarm?.Cancel();
            _prewarm = null;
        }
    }

    /// <summary>The prompt for <paramref name="request"/>. The whole output allowance has to fit beside it.</summary>
    private static string RenderPrompt(SuggestionRequest request, LlamaRuntimeCore core) =>
        CompletionPipeline.Prompt(
            request,
            PromptTokenBudget.ForRequest(PromptTokenBudget.Configured(core.ContextWindowTokens), request.MaxTokens));

    private static LlamaGenerationOptions OptionsFor(SuggestionRequest request) => new()
    {
        MaxTokens = request.MaxTokens,
        SingleLine = request.SingleLine,
        SentenceStopMinimumWords = request.WordRange?.LowWords ?? 0,
    };
}
