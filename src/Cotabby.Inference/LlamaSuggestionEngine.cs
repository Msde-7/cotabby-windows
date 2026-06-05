using System.Runtime.CompilerServices;
using System.Text;
using Cotabby.Core.Suggestions;
using LLama.Common;
using LLama.Sampling;
using Microsoft.Extensions.Logging;

namespace Cotabby.Inference;

/// <summary>
/// LLamaSharp-backed <see cref="ISuggestionEngine"/>. Translates the pure
/// <see cref="SuggestionRequest"/> into a Qwen-style prompt, streams tokens
/// out of the runtime, applies post-processing (stop-on-newline for single-
/// line fields, trim whitespace-only tails, drop self-echo prefix), and
/// surfaces them as <see cref="SuggestionChunk"/> values.
/// </summary>
/// <remarks>
/// Prompt strategy follows the macOS port: fill-in-middle is reserved for
/// genuinely mid-line carets (real text follows the caret on the same line);
/// every other case uses bare prefix continuation, which is far more stable
/// on instruct-tuned Qwen variants — the Instruct fine-tune frequently
/// ignores or mis-applies <c>&lt;|fim_*|&gt;</c> tokens and degenerates into
/// single-token loops.
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
        if (!_runtime.IsReady || _runtime.Executor is null)
        {
            _logger.LogWarning("Generate called while runtime not ready (req={ReqId}).", request.RequestId);
            yield return new SuggestionChunk(string.Empty, IsFinal: true);
            yield break;
        }

        var prompt = BuildPrompt(request);
        var antiPrompts = request.SingleLine
            ? new[] { "\n", "<|endoftext|>", "<|fim_pad|>", "<|im_end|>" }
            : new[] { "\n\n", "<|endoftext|>", "<|fim_pad|>", "<|im_end|>" };

        var inference = new InferenceParams
        {
            MaxTokens = request.MaxTokens,
            AntiPrompts = antiPrompts,
            SamplingPipeline = new DefaultSamplingPipeline
            {
                // Code completion wants tight sampling: low temperature so the
                // most likely continuation wins, narrow top-k/p so the tail of
                // the distribution can't surface noise, mild repetition penalty
                // (the upstream macOS port runs the same values). Anything
                // hotter and the 1.5B model wanders into pseudocode/prose.
                Temperature = 0.1f,
                TopP = 0.7f,
                MinP = 0.08f,
                TopK = 20,
                RepeatPenalty = 1.05f,
                PenaltyCount = 64,
            },
        };

        var emitted = new StringBuilder();
        bool firstChunk = true;
        const int maxRunLength = 4;     // stop on 5+ identical non-whitespace chars
        // Cap absolute output. MaxTokens is in tokens, not chars, and BPE can
        // pack 4-5 chars into one token, so an output that loops on a 4-char
        // token easily blows past the visible budget. Stop at 100 chars —
        // ghost text any longer than that just runs off the side of the host
        // editor before the user can read it.
        const int maxOutputChars = 100;

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

            // Drop chat-style refusals and assistant greetings the moment they
            // surface. The bartowski Qwen-Coder GGUFs are all Instruct
            // fine-tunes; on bare-prefix continuation (caret at end-of-line,
            // no FIM suffix) they treat the user's prose as a chat message and
            // respond as the assistant. Always-FIM would suppress this but
            // crashes LLamaSharp 0.21's sampler chain on empty suffix. Until
            // that's fixed upstream, scrub the recognizable patterns instead.
            if (LooksLikeChatLeak(emitted))
            {
                _logger.LogInformation(
                    "Stopping {ReqId} early: instruct chat-leak pattern detected (\"{Preview}\").",
                    request.RequestId, emitted.ToString());
                yield break;
            }

            if (emitted.Length > maxOutputChars)
            {
                _logger.LogInformation(
                    "Stopping {ReqId} at {Chars}-char absolute cap.",
                    request.RequestId, emitted.Length);
                yield break;
            }

            // Degeneration guards. Small models lock into either:
            //   (a) a single token loop ("aaaa…", "I I I I …")
            //   (b) a multi-token phrase loop ("hi my name is gabe hi my name
            //       is gabe hi my name is gabe…")
            if (HasLongCharRun(emitted, maxRunLength))
            {
                _logger.LogInformation(
                    "Stopping {ReqId} early at {Chars} chars: long char run detected ('{Last}').",
                    request.RequestId, emitted.Length, emitted[^1]);
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
        // FIM is the right template when the caret is genuinely mid-line —
        // real text follows the caret on the same line. Always-FIM (wrapping
        // every prompt in <|fim_prefix|>…<|fim_suffix|><|fim_middle|>, even
        // at end-of-line where the suffix is empty or a synthetic newline)
        // crashes LLamaSharp 0.21's StatelessExecutor sampler chain with
        // "invalid logits id N, reason: batch.logits[N] != true" (0xC0000005
        // at SafeLLamaSamplerChainHandle.Sample). Reproduced consistently on
        // the 3B-Instruct and 0.5B-Instruct quantizations; the bug is in
        // how StatelessExecutor sets up the batch when FIM markers tokenize
        // into multi-segment positions. Until that's fixed upstream we can
        // only safely use FIM when the suffix is non-empty real text.
        if (HasMeaningfulSuffix(request.Suffix))
        {
            return $"<|fim_prefix|>{request.Prefix}<|fim_suffix|>{request.Suffix}<|fim_middle|>";
        }
        // End-of-line fall-through: bare prefix continuation. The Instruct
        // fine-tune treats a bare conversational prefix ("Hi I am") as a
        // user turn and replies as the assistant. Trim trailing whitespace
        // so the model starts at a real token boundary instead of re-emitting
        // the trailing space.
        return TrimTrailingWhitespace(request.Prefix);
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

    private static string TrimTrailingWhitespace(string s)
    {
        int end = s.Length;
        while (end > 0 && (s[end - 1] == ' ' || s[end - 1] == '\t')) end--;
        return end == s.Length ? s : s[..end];
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
    /// Recognizable assistant/chat patterns an Instruct fine-tune emits when
    /// given bare prose as a "user turn." These never make sense as inline
    /// autocomplete and almost certainly aren't what the user typed next;
    /// better to suppress the suggestion than insert a refusal.
    /// </summary>
    private static readonly string[] ChatLeakPrefixes = new[]
    {
        // Refusals
        "I'm sorry, but I can",
        "I'm sorry, I can",
        "I cannot",
        "I can't assist",
        "I can't help",
        "Sorry, I can't",
        "Sorry, but I can",
        // Generic assistant greetings
        "Hi there! How can I",
        "Hello! How can I",
        "Hi! How can I",
        "Hello there! How can",
        "How can I assist",
        "How can I help you",
        // Common Coder-Instruct opener when treating prose as a code request
        "Sure, here is",
        "Sure, here's",
        "Certainly! Here",
    };

    /// <summary>
    /// Tests the LEAD of <paramref name="sb"/> (skipping leading whitespace and
    /// comment markers like '#', '//', '*') against <see cref="ChatLeakPrefixes"/>.
    /// Case-insensitive substring match against a 32-char window so a chat
    /// pattern that surfaces inside a code-comment prefix ("#I'm sorry…") is
    /// still caught.
    /// </summary>
    private static bool LooksLikeChatLeak(StringBuilder sb)
    {
        if (sb.Length < 6) return false;
        int start = 0;
        while (start < sb.Length && (sb[start] == ' ' || sb[start] == '#' ||
                                     sb[start] == '/' || sb[start] == '*' ||
                                     sb[start] == '-' || sb[start] == '>'))
        {
            start++;
        }
        if (start >= sb.Length) return false;
        int windowLen = Math.Min(32, sb.Length - start);
        var window = new char[windowLen];
        for (int i = 0; i < windowLen; i++) window[i] = sb[start + i];
        var winStr = new string(window);
        foreach (var pat in ChatLeakPrefixes)
        {
            if (winStr.Length >= pat.Length &&
                winStr.AsSpan(0, pat.Length).Equals(pat.AsSpan(), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// True if anywhere in <paramref name="sb"/> there is a run of more than
    /// <paramref name="threshold"/> identical non-whitespace characters. Scans
    /// the WHOLE buffer rather than just the tail because BPE can pack a long
    /// 'sssssss' run into a single token, AND the model can interleave a few
    /// non-'s' chars between 's' chunks so the run never quite ends up at the
    /// tail when each chunk lands.
    ///
    /// Whitespace runs (indentation, blank lines) are skipped — Python and
    /// YAML routinely produce long whitespace sequences as legitimate output.
    /// </summary>
    private static bool HasLongCharRun(StringBuilder sb, int threshold)
    {
        if (sb.Length <= threshold) return false;
        int run = 1;
        char prev = sb[0];
        for (int i = 1; i < sb.Length; i++)
        {
            char c = sb[i];
            if (c == prev)
            {
                run++;
                if (run > threshold && prev != ' ' && prev != '\t' && prev != '\n' && prev != '\r')
                {
                    return true;
                }
            }
            else
            {
                run = 1;
                prev = c;
            }
        }
        return false;
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
