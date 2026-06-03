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
                // 0.5B Qwen-Coder degenerates fast at low temperature — single-
                // token loops ("I I I I", "aaaa..."). Higher temp + repetition
                // penalty + top-k make degeneration far less likely on small
                // models. These values are the llama.cpp defaults for code
                // completion.
                Temperature = 0.5f,
                TopP = 0.9f,
                TopK = 40,
                RepeatPenalty = 1.2f,
                PenaltyCount = 64,
            },
        };

        var emitted = new StringBuilder();
        bool firstChunk = true;
        const int maxRunLength = 8; // stop on 9+ identical chars in a row

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

            // Degeneration guard: small models occasionally lock into a single
            // token ("aaaaaaaa..." / "I I I I I..."). If we've emitted a long
            // run of the same character, stop instead of polluting the field.
            if (HasLongCharRun(emitted, maxRunLength))
            {
                _logger.LogInformation(
                    "Stopping {ReqId} early: degenerate run detected.",
                    request.RequestId);
                yield break;
            }

            yield return new SuggestionChunk(clean, IsFinal: false);
        }

        yield return new SuggestionChunk(string.Empty, IsFinal: true);
        _logger.LogDebug(
            "Generation complete: req={ReqId} emitted={Chars} chars.",
            request.RequestId, emitted.Length);
    }

    private static string BuildPrompt(SuggestionRequest request)
    {
        // Qwen2.5-Coder fill-in-middle template. Always use FIM so the model
        // knows it's emitting a span between two boundaries, even when the
        // suffix is empty. Without FIM the instruct-tuned 0.5B model frequently
        // outputs degenerate runs because raw-prefix continuation isn't its
        // training format. Reference: https://qwenlm.github.io/blog/qwen2.5-coder/
        return $"<|fim_prefix|>{request.Prefix}<|fim_suffix|>{request.Suffix}<|fim_middle|>";
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

    /// <summary>
    /// Walk back from the end of <paramref name="sb"/> and stop as soon as the
    /// last char doesn't match; if the matching run is &gt; <paramref name="threshold"/>,
    /// return true. Detects "aaaaaaaa" and similar collapses cheaply (O(threshold))
    /// without scanning the whole emitted buffer.
    /// </summary>
    private static bool HasLongCharRun(StringBuilder sb, int threshold)
    {
        if (sb.Length <= threshold) return false;
        char last = sb[^1];
        int count = 0;
        for (int i = sb.Length - 1; i >= 0 && count <= threshold; i--)
        {
            if (sb[i] != last) return false;
            count++;
        }
        return count > threshold;
    }
}
