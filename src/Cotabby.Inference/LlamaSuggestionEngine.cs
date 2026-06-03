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

            // Degeneration guards. Small models lock into either:
            //   (a) a single token loop ("aaaa…", "I I I I …")
            //   (b) a multi-token phrase loop ("hi my name is gabe hi my name
            //       is gabe hi my name is gabe…")
            // Both are catastrophic for the user — the suggestion fills with
            // junk that they have no way to dismiss without typing. Catch (a)
            // with HasLongCharRun and (b) with HasRepeatingPattern.
            if (HasLongCharRun(emitted, maxRunLength))
            {
                _logger.LogInformation(
                    "Stopping {ReqId} early: long char run detected.",
                    request.RequestId);
                yield break;
            }
            if (HasRepeatingPattern(emitted))
            {
                _logger.LogInformation(
                    "Stopping {ReqId} early: repeating phrase detected.",
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
    /// without scanning the whole emitted buffer. Whitespace runs (indentation,
    /// blank lines) are NEVER flagged — Python and YAML routinely produce long
    /// whitespace sequences as legitimate output.
    /// </summary>
    private static bool HasLongCharRun(StringBuilder sb, int threshold)
    {
        if (sb.Length <= threshold) return false;
        char last = sb[^1];
        if (last == ' ' || last == '\t' || last == '\n' || last == '\r') return false;
        int count = 0;
        for (int i = sb.Length - 1; i >= 0 && count <= threshold; i--)
        {
            if (sb[i] != last) return false;
            count++;
        }
        return count > threshold;
    }

    /// <summary>
    /// Detects whether the tail of <paramref name="sb"/> contains a repeating
    /// pattern of N characters. Multi-token loops like
    /// "hi my name is gabe hi my name is gabe…" slip past
    /// <see cref="HasLongCharRun"/> because no single character repeats.
    ///
    /// Heuristics:
    /// <list type="bullet">
    /// <item>Short patterns (N &lt; 10) need 3 consecutive copies — "foo foo
    /// foo" is degenerate, but a one-off "foo foo" might be legitimate.</item>
    /// <item>Long patterns (N &gt;= 10) need only 2 — repeating a whole phrase
    /// is almost never legitimate output.</item>
    /// </list>
    /// </summary>
    private static bool HasRepeatingPattern(StringBuilder sb, int maxPatternLen = 40, int minPatternLen = 3)
    {
        int len = sb.Length;
        if (len < minPatternLen * 2) return false;
        int maxN = Math.Min(maxPatternLen, len / 2);
        for (int n = minPatternLen; n <= maxN; n++)
        {
            bool match = true;
            for (int i = 0; i < n; i++)
            {
                if (sb[len - 1 - i] != sb[len - 1 - i - n]) { match = false; break; }
            }
            if (!match) continue;

            // Two copies of a long phrase (>= 10 chars) is already enough.
            if (n >= 10) return true;

            // For short patterns, require a third confirming copy.
            if (len >= n * 3)
            {
                bool third = true;
                for (int i = 0; i < n; i++)
                {
                    if (sb[len - 1 - i - n] != sb[len - 1 - i - n * 2]) { third = false; break; }
                }
                if (third) return true;
            }
        }
        return false;
    }
}
