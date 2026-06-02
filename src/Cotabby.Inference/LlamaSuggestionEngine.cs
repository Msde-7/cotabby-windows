using System.Runtime.CompilerServices;
using System.Text;
using Cotabby.Core.Suggestions;
using LLama.Common;
using LLama.Sampling;
using Microsoft.Extensions.Logging;

namespace Cotabby.Inference;

/// <summary>
/// LLamaSharp-backed <see cref="ISuggestionEngine"/>. Translates the pure
/// <see cref="SuggestionRequest"/> into a Qwen-style fill-in-middle prompt,
/// streams tokens out of the runtime, applies post-processing (stop-on-newline
/// for single-line fields, trim whitespace-only tails, drop self-echo prefix),
/// and surfaces them as <see cref="SuggestionChunk"/> values.
/// </summary>
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
        if (!_runtime.IsReady || _runtime.Executor is null)
        {
            _logger.LogWarning("Generate called while runtime not ready (req={ReqId}).", request.RequestId);
            yield return new SuggestionChunk(string.Empty, IsFinal: true);
            yield break;
        }

        var prompt = BuildPrompt(request);
        var antiPrompts = request.SingleLine ? new[] { "\n", "<|endoftext|>", "<|fim_pad|>" }
                                              : new[] { "<|endoftext|>", "<|fim_pad|>" };

        var inference = new InferenceParams
        {
            MaxTokens = request.MaxTokens,
            AntiPrompts = antiPrompts,
            SamplingPipeline = new DefaultSamplingPipeline
            {
                Temperature = 0.2f,
                TopP = 0.95f,
            },
        };

        var emitted = new StringBuilder();
        bool firstChunk = true;

        await foreach (var token in _runtime.Executor.InferAsync(prompt, inference, ct).ConfigureAwait(false))
        {
            if (ct.IsCancellationRequested) yield break;

            var clean = token;

            // The stateless executor occasionally re-emits the prompt's last
            // characters as the first chunk; strip self-echo so the overlay
            // doesn't duplicate what the user just typed.
            if (firstChunk)
            {
                firstChunk = false;
                clean = StripEchoedPrefix(clean, request.Prefix);
            }

            if (string.IsNullOrEmpty(clean)) continue;

            emitted.Append(clean);
            yield return new SuggestionChunk(clean, IsFinal: false);
        }

        yield return new SuggestionChunk(string.Empty, IsFinal: true);
        _logger.LogDebug(
            "Generation complete: req={ReqId} emitted={Chars} chars.",
            request.RequestId, emitted.Length);
    }

    private static string BuildPrompt(SuggestionRequest request)
    {
        // Qwen2.5-Coder fill-in-middle template. Even when the suffix is empty
        // FIM works correctly — the model just treats it as plain continuation.
        // Reference: https://qwenlm.github.io/blog/qwen2.5-coder/
        if (!string.IsNullOrEmpty(request.Suffix))
        {
            return $"<|fim_prefix|>{request.Prefix}<|fim_suffix|>{request.Suffix}<|fim_middle|>";
        }
        return request.Prefix;
    }

    private static string StripEchoedPrefix(string chunk, string prefix)
    {
        // Find the longest suffix of `prefix` that is also a prefix of `chunk`.
        int max = Math.Min(prefix.Length, chunk.Length);
        for (int n = max; n > 0; n--)
        {
            if (chunk.AsSpan(0, n).SequenceEqual(prefix.AsSpan(prefix.Length - n)))
            {
                return chunk[n..];
            }
        }
        return chunk;
    }
}
