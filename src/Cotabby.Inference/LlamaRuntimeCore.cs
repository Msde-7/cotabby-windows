using System.Diagnostics;
using System.Text;
using Cotabby.Core.Support;
using LLama;
using LLama.Native;
using Microsoft.Extensions.Logging;

namespace Cotabby.Inference;

/// <summary>Options for a single constrained generation.</summary>
public sealed record LlamaGenerationOptions
{
    public required int MaxTokens { get; init; }
    public bool SingleLine { get; init; }
    /// <summary>Mask tokens that start a new word for the first sampled token
    /// (caret is mid-word).</summary>
    public bool ForceWordContinuation { get; init; }
    /// <summary>Argmax candidate pool size. Mirrors upstream topK = 20.</summary>
    public int TopK { get; init; } = 20;
}

/// <summary>
/// Owns one persistent llama.cpp context and implements the upstream
/// constrained decode: deterministic argmax over the top-K raw logits, with
/// control tokens masked, a no-repeat-3-gram guard, and stops on EOS /
/// newline (single-line) / sentence boundary / token budget. Direct port of
/// the macOS <c>LlamaRuntimeCore.runConstrainedDecode</c>.
/// </summary>
/// <remarks>
/// A persistent context is the entire point of this type: the old
/// StatelessExecutor path re-created the llama context (1-3 s of graph
/// compilation on Vulkan) for every keystroke's request. Here the context is
/// built once at model load; each generation just clears the sequence memory
/// and evaluates the prompt, so time-to-first-suggestion is prompt-eval +
/// decode only. All calls are serialized by an internal gate — llama.cpp
/// contexts are not thread-safe.
/// </remarks>
public sealed class LlamaRuntimeCore : IDisposable
{
    private const int NoRepeatNgramSize = 3;

    private readonly LLamaWeights _weights;
    private readonly LLamaContext _context;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly bool _canReusePrefix;
    private LLamaToken[] _lastPromptTokens = [];
    private bool _disposed;

    public LlamaRuntimeCore(LLamaWeights weights, LLamaContext context, ILogger logger)
    {
        _weights = weights;
        _context = context;
        _logger = logger;
        // Recurrent/hybrid models (Qwen3.5's gated delta net) cannot drop a
        // suffix of their state, so prefix reuse is transformer-only.
        _canReusePrefix = !weights.NativeHandle.IsRecurrent && context.NativeHandle.MemoryCanShift;
    }

    public SafeLlamaModelHandle.Vocabulary Vocab => _weights.Vocab;

    /// <summary>True if the loaded model exposes all three FIM markers.</summary>
    public bool SupportsFillInMiddle =>
        Vocab.InfillPrefix.HasValue && Vocab.InfillSuffix.HasValue && Vocab.InfillMiddle.HasValue;

    /// <summary>Tokenize plain user text (special tokens NOT parsed).</summary>
    public LLamaToken[] TokenizeText(string text, bool addBos) =>
        _context.NativeHandle.Tokenize(text, add_bos: addBos, special: false, Encoding.UTF8);

    /// <summary>
    /// Assemble a FIM token sequence: &lt;fim_prefix&gt; prefix &lt;fim_suffix&gt; suffix &lt;fim_middle&gt;.
    /// The suffix keeps its head (nearest the caret) and the prefix its tail.
    /// </summary>
    public LLamaToken[] BuildFimTokens(string prefix, string suffix, int maxPromptTokens)
    {
        var prefixTokens = TokenizeText(prefix, addBos: false);
        var suffixTokens = TokenizeText(suffix, addBos: false);
        int budget = Math.Max(0, maxPromptTokens - 3);
        int suffixKeep = Math.Min(suffixTokens.Length, budget / 2);
        int prefixKeep = Math.Min(prefixTokens.Length, budget - suffixKeep);

        var tokens = new List<LLamaToken>(prefixKeep + suffixKeep + 3)
        {
            Vocab.InfillPrefix!.Value,
        };
        tokens.AddRange(prefixTokens[^prefixKeep..]);
        tokens.Add(Vocab.InfillSuffix!.Value);
        tokens.AddRange(suffixTokens[..suffixKeep]);
        tokens.Add(Vocab.InfillMiddle!.Value);
        return tokens.ToArray();
    }

    /// <summary>
    /// Run one constrained generation over <paramref name="promptTokens"/>.
    /// Returns raw (un-normalized) text; empty when nothing admissible was
    /// produced or the caller cancelled before the first token.
    /// </summary>
    public async Task<string> GenerateAsync(
        LLamaToken[] promptTokens, LlamaGenerationOptions options, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => Generate(promptTokens, options, ct), CancellationToken.None)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private string Generate(LLamaToken[] promptTokens, LlamaGenerationOptions options, CancellationToken ct)
    {
        var handle = _context.NativeHandle;
        var seq = (LLamaSeqId)0;
        var sw = Stopwatch.StartNew();

        // Window the prompt so there is always room for the prediction.
        int maxPromptTokens = Math.Max(1, (int)handle.ContextSize - options.MaxTokens);
        if (promptTokens.Length > maxPromptTokens)
        {
            promptTokens = promptTokens[^maxPromptTokens..];
        }
        if (promptTokens.Length == 0) return string.Empty;

        // Prefix reuse: keep the longest common token prefix with the previous
        // prompt resident in KV and evaluate only the divergent tail. When the
        // model can't shift (recurrent state), clear and re-evaluate fully.
        int evalFrom = 0;
        if (_canReusePrefix && _lastPromptTokens.Length > 0)
        {
            int common = 0;
            int limit = Math.Min(_lastPromptTokens.Length, promptTokens.Length);
            while (common < limit && _lastPromptTokens[common] == promptTokens[common]) common++;
            // Always re-evaluate at least the final token so logits are fresh.
            common = Math.Min(common, promptTokens.Length - 1);
            if (common > 0)
            {
                handle.MemorySequenceRemove(seq, common, -1);
                evalFrom = common;
            }
            else
            {
                handle.MemoryClear(true);
            }
        }
        else
        {
            handle.MemoryClear(true);
        }

        var batch = new LLamaBatch();
        int batchSize = (int)handle.BatchSize;
        int lastLogitsIndex = 0;
        for (int i = evalFrom; i < promptTokens.Length; i += batchSize)
        {
            if (ct.IsCancellationRequested) return string.Empty;
            batch.Clear();
            int end = Math.Min(promptTokens.Length, i + batchSize);
            for (int j = i; j < end; j++)
            {
                batch.Add(promptTokens[j], j, seq, logits: j == promptTokens.Length - 1);
            }
            var result = handle.Decode(batch);
            if (result != DecodeResult.Ok)
            {
                _logger.LogWarning("Prompt decode failed: {Result}; clearing memory.", result);
                handle.MemoryClear(true);
                _lastPromptTokens = [];
                return string.Empty;
            }
            lastLogitsIndex = end - i - 1;
        }
        long prefillMs = sw.ElapsedMilliseconds;

        var generatedBytes = new List<byte>(256);
        var generatedIds = new List<int>(options.MaxTokens);
        var scratch = new byte[512];
        string stopReason = "budget";

        for (int step = 0; step < options.MaxTokens; step++)
        {
            if (ct.IsCancellationRequested) { stopReason = "cancelled"; break; }

            var logits = handle.GetLogitsIth(lastLogitsIndex);
            var blocked = RepetitionGuard.BlockedTokens(generatedIds, NoRepeatNgramSize);
            var token = SelectToken(logits, options.TopK, blocked,
                forceWordContinuation: options.ForceWordContinuation && step == 0, scratch);
            if (token is null) { stopReason = "no_admissible_token"; break; }

            var chosen = token.Value;
            if (chosen.IsEndOfGeneration(Vocab)) { stopReason = "eos"; break; }

            uint written = handle.TokenToSpan(chosen, scratch);
            int byteLen = (int)Math.Min(written, (uint)scratch.Length);
            if (options.SingleLine && ContainsNewline(scratch.AsSpan(0, byteLen)))
            {
                stopReason = "single_line";
                break;
            }

            for (int b = 0; b < byteLen; b++) generatedBytes.Add(scratch[b]);
            generatedIds.Add((int)chosen);

            batch.Clear();
            batch.Add(chosen, promptTokens.Length + generatedIds.Count - 1, seq, logits: true);
            if (handle.Decode(batch) != DecodeResult.Ok) { stopReason = "accept_failed"; break; }
            lastLogitsIndex = 0;

            // Stop at a natural sentence boundary — only worth classifying
            // when this token could have completed one.
            if (ContainsSentenceTerminator(scratch.AsSpan(0, byteLen)))
            {
                var soFar = Encoding.UTF8.GetString(generatedBytes.ToArray());
                if (SentenceBoundaryClassifier.EndsSentence(soFar)) { stopReason = "sentence_boundary"; break; }
            }
        }

        // Keep only the prompt in memory so the next request can reuse it.
        if (_canReusePrefix)
        {
            handle.MemorySequenceRemove(seq, promptTokens.Length, -1);
            _lastPromptTokens = promptTokens;
        }
        else
        {
            _lastPromptTokens = [];
        }

        var text = Encoding.UTF8.GetString(generatedBytes.ToArray());
        _logger.LogInformation(
            "Constrained decode: {Tokens} tokens in {Ms} ms (prefill {Prefill} ms, reuse {Reuse}/{Total}), stop={Stop}.",
            generatedIds.Count, sw.ElapsedMilliseconds, prefillMs, evalFrom, promptTokens.Length, stopReason);
        return text;
    }

    /// <summary>
    /// Deterministic constrained selection: candidate pool = top-K token ids
    /// by raw logit; walk the pool skipping control/blocked (and, on a forced
    /// mid-word first step, tokens that start a new word); pick the
    /// highest-logit survivor. Mirrors the macOS <c>ConstrainedSampler</c>.
    /// </summary>
    private LLamaToken? SelectToken(
        ReadOnlySpan<float> logits, int topK, HashSet<int> blocked,
        bool forceWordContinuation, byte[] scratch)
    {
        if (topK <= 0) return null;
        int k = Math.Min(topK, logits.Length);

        // Partial top-K selection: fixed arrays, single pass over the vocab.
        Span<int> ids = stackalloc int[64];
        Span<float> vals = stackalloc float[64];
        k = Math.Min(k, 64);
        int count = 0;
        for (int i = 0; i < logits.Length; i++)
        {
            float v = logits[i];
            if (count == k && v <= vals[count - 1]) continue;
            // Insertion into the sorted (desc) top-k window.
            int pos = count < k ? count : k - 1;
            while (pos > 0 && vals[pos - 1] < v)
            {
                if (pos < k)
                {
                    vals[pos] = vals[pos - 1];
                    ids[pos] = ids[pos - 1];
                }
                pos--;
            }
            vals[pos] = v;
            ids[pos] = i;
            if (count < k) count++;
        }

        for (int i = 0; i < count; i++)
        {
            var token = (LLamaToken)ids[i];
            if (blocked.Contains(ids[i])) continue;
            if (token.IsControl(Vocab)) continue;

            uint written = _context.NativeHandle.TokenToSpan(token, scratch);
            int byteLen = (int)Math.Min(written, (uint)scratch.Length);
            if (byteLen == 0) continue;

            if (forceWordContinuation && !ContinuesWordMidStream(scratch.AsSpan(0, byteLen))) continue;
            return token;
        }
        return null;
    }

    /// <summary>First byte continues the current word: letter, digit,
    /// apostrophe, hyphen, or a non-ASCII lead byte.</summary>
    private static bool ContinuesWordMidStream(ReadOnlySpan<byte> bytes)
    {
        byte b = bytes[0];
        return b is (>= (byte)'a' and <= (byte)'z') or (>= (byte)'A' and <= (byte)'Z')
            or (>= (byte)'0' and <= (byte)'9') or (byte)'\'' or (byte)'-'
            || b >= 0x80;
    }

    private static bool ContainsNewline(ReadOnlySpan<byte> bytes)
    {
        foreach (var b in bytes)
        {
            if (b == (byte)'\n') return true;
        }
        return false;
    }

    private static bool ContainsSentenceTerminator(ReadOnlySpan<byte> bytes)
    {
        foreach (var b in bytes)
        {
            if (b is (byte)'.' or (byte)'!' or (byte)'?') return true;
        }
        return false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _context.Dispose();
        _gate.Dispose();
    }
}
