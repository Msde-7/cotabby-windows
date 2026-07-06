using System.Runtime.CompilerServices;
using Cotabby.Core.Support;
using Cotabby.Core.Suggestions;
using Microsoft.Extensions.Logging;

namespace Cotabby.Inference;

/// <summary>
/// LLamaSharp-backed <see cref="ISuggestionEngine"/>. Mirrors the macOS
/// llama path: render a base-model conditioning prompt (persona / language
/// preface + caret prefix), run one constrained greedy decode on the
/// persistent runtime core, normalize the result once, and surface it as a
/// single final chunk.
/// </summary>
/// <remarks>
/// There is intentionally no token streaming: the upstream app presents the
/// whole normalized suggestion at once, because half the quality problems
/// (prompt echo, chat leaks, trailing dups) are only detectable on the full
/// string. Emitting one final chunk keeps the coordinator's streaming
/// interface intact while matching upstream behavior.
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

        // FIM is used only when the caret is genuinely mid-line (real text
        // follows on the same line) AND the model exposes infill markers.
        // Everything else goes through the base continuation prompt.
        bool useFim = core.SupportsFillInMiddle && HasMeaningfulSuffix(request.Suffix);

        string prompt;
        LLama.Native.LLamaToken[] promptTokens;
        if (useFim)
        {
            prompt = request.Prefix; // for normalization echo checks
            int maxPromptTokens = Math.Max(1, 4096 - request.MaxTokens);
            promptTokens = core.BuildFimTokens(request.Prefix, request.Suffix, maxPromptTokens);
        }
        else
        {
            var windowedPrefix = BaseCompletionPromptRenderer.WindowPrefix(request.Prefix);
            prompt = BaseCompletionPromptRenderer.Prompt(
                windowedPrefix,
                userName: request.UserName,
                languageInstruction: request.LanguageInstruction);
            if (prompt.Length == 0)
            {
                yield return new SuggestionChunk(string.Empty, IsFinal: true);
                yield break;
            }
            promptTokens = core.TokenizeText(prompt, addBos: core.Vocab.ShouldAddBOS);
        }

        string raw;
        try
        {
            raw = await core.GenerateAsync(promptTokens, options, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            yield break;
        }

        if (ct.IsCancellationRequested) yield break;

        var normalized = SuggestionTextNormalizer.Normalize(
            raw, prompt, request.Prefix, request.Suffix, request.SingleLine);

        if (normalized.IsSuppressed)
        {
            if (normalized.Reason != SuppressionReason.EmptyGeneration)
            {
                _logger.LogInformation(
                    "Req {ReqId}: suppressed ({Reason}); raw=\"{Raw}\".",
                    request.RequestId, normalized.Reason, Preview(raw));
            }
            yield return new SuggestionChunk(string.Empty, IsFinal: true);
            yield break;
        }

        _logger.LogInformation(
            "Req {ReqId}: suggestion \"{Text}\" (raw {RawLen} chars).",
            request.RequestId, Preview(normalized.Text), raw.Length);
        yield return new SuggestionChunk(normalized.Text, IsFinal: false);
        yield return new SuggestionChunk(string.Empty, IsFinal: true);
    }

    private static bool HasMeaningfulSuffix(string suffix)
    {
        if (string.IsNullOrEmpty(suffix)) return false;
        for (int i = 0; i < suffix.Length; i++)
        {
            char c = suffix[i];
            if (c == '\n' || c == '\r') return false;
            if (!char.IsWhiteSpace(c)) return true;
        }
        return false;
    }

    private static string Preview(string s)
    {
        var flat = s.Replace("\n", "\\n").Replace("\r", "");
        return flat.Length <= 80 ? flat : flat[..80] + "…";
    }
}
