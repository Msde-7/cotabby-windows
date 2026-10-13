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

    /// <summary>Mask tokens that start a new word for the first sampled token (caret is mid-word).</summary>
    public bool ForceWordContinuation { get; init; }

    /// <summary>Argmax candidate pool size.</summary>
    public int TopK { get; init; } = 20;

    /// <summary>Minimum tokens generated before a sentence end may stop decoding.</summary>
    public int SentenceStopMinimumTokens { get; init; } = 2;

    /// <summary>
    /// Minimum whitespace-separated words before a sentence end may stop decoding. The word-count
    /// preset's lower bound, so a short sentence end does not cut a suggestion to one word.
    /// </summary>
    public int SentenceStopMinimumWords { get; init; }
}

/// <summary>
/// Owns one persistent llama.cpp context and implements the constrained decode. Token choice is a
/// deterministic argmax over the top-K raw logits with control tokens masked and a no-repeat 3-gram
/// guard. Decoding stops on end of generation, a newline in single-line mode, the
/// <see cref="DecodeStopPolicy"/> verdict or the token budget.
/// </summary>
/// <remarks>
/// The context is built once at model load. Each generation keeps the longest shared prompt prefix
/// in memory and evaluates only the rest, so time to first suggestion is prompt evaluation plus
/// decode. All calls are serialized by an internal gate because llama.cpp contexts are not
/// thread-safe.
/// </remarks>
public sealed class LlamaRuntimeCore : IDisposable
{
    private const int NoRepeatNgramSize = 3;

    private readonly LLamaWeights _weights;
    private readonly LLamaContext _context;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly bool _canReusePrefix;
    private readonly TokenHealingVocabulary _healingVocabulary;
    private LLamaToken[] _lastPromptTokens = [];
    private bool _disposed;

    public LlamaRuntimeCore(LLamaWeights weights, LLamaContext context, ILogger logger)
    {
        _weights = weights;
        _context = context;
        _logger = logger;
        // Recurrent and hybrid models cannot drop a suffix of their state, so prefix reuse is
        // limited to models whose memory can shift.
        _canReusePrefix = !weights.NativeHandle.IsRecurrent && context.NativeHandle.MemoryCanShift;
        _healingVocabulary = BuildHealingVocabulary();
    }

    public SafeLlamaModelHandle.Vocabulary Vocab => _weights.Vocab;

    /// <summary>Size of the context window in tokens.</summary>
    public int ContextWindowTokens => (int)_context.NativeHandle.ContextSize;

    /// <summary>True if the loaded model exposes all three fill-in-the-middle markers.</summary>
    public bool SupportsFillInMiddle =>
        Vocab.InfillPrefix.HasValue && Vocab.InfillSuffix.HasValue && Vocab.InfillMiddle.HasValue;

    /// <summary>Tokenize plain user text. Special tokens are not parsed.</summary>
    public LLamaToken[] TokenizeText(string text, bool addBos) =>
        _context.NativeHandle.Tokenize(text, add_bos: addBos, special: false, Encoding.UTF8);

    /// <summary>
    /// Assemble a fill-in-the-middle token sequence of prefix marker, prefix, suffix marker, suffix
    /// and middle marker. The suffix keeps its head and the prefix its tail, the text nearest the caret.
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
    /// Run one constrained generation that continues <paramref name="prompt"/>. The prompt's final
    /// word is healed, so the model may re-spell the typed fragment with a token that also carries
    /// its continuation. Returns raw text, empty when nothing admissible was produced.
    /// </summary>
    public Task<string> GenerateAsync(string prompt, LlamaGenerationOptions options, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var tokens = Array.ConvertAll(TokenizeText(prompt, addBos: Vocab.ShouldAddBOS), token => (int)token);
        // A caret inside a word keeps the unhealed prompt so the new-word mask stays in force.
        var healing = options.ForceWordContinuation
            ? TokenHealingPlan.Unhealed(tokens)
            : new TokenHealingPlan(prompt, tokens, options.SingleLine, token => TokenPiece(token));
        var promptTokens = healing.PromptTokens.Select(token => (LLamaToken)token).ToArray();
        return GenerateAsync(promptTokens, healing.ReplayBytes, options, ct);
    }

    /// <summary>
    /// Run one constrained generation over <paramref name="promptTokens"/> as given. Returns raw
    /// text, empty when nothing admissible was produced or the caller cancelled before the first token.
    /// </summary>
    public Task<string> GenerateAsync(
        LLamaToken[] promptTokens, LlamaGenerationOptions options, CancellationToken ct) =>
        GenerateAsync(promptTokens, [], options, ct);

    private async Task<string> GenerateAsync(
        LLamaToken[] promptTokens, byte[] healingPrefix, LlamaGenerationOptions options, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => Generate(promptTokens, healingPrefix, options, ct), CancellationToken.None)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private string Generate(
        LLamaToken[] promptTokens, byte[] healingPrefix, LlamaGenerationOptions options, CancellationToken ct)
    {
        var handle = _context.NativeHandle;
        var seq = (LLamaSeqId)0;
        var sw = Stopwatch.StartNew();

        // The whole decode is reserved before the prompt is admitted. A byte-fallback vocabulary
        // can replay one token per healed byte, so the replay has its own allowance and cannot
        // consume the output budget.
        int maxPromptTokens = Math.Max(1, (int)handle.ContextSize - options.MaxTokens - healingPrefix.Length);
        if (promptTokens.Length > maxPromptTokens)
        {
            promptTokens = promptTokens[^maxPromptTokens..];
        }
        if (promptTokens.Length == 0) return string.Empty;

        // The longest common token prefix with the previous prompt stays in memory and only the
        // divergent tail is evaluated. A model that cannot shift is cleared and evaluated fully.
        int evalFrom = 0;
        if (_canReusePrefix && _lastPromptTokens.Length > 0)
        {
            int common = 0;
            int limit = Math.Min(_lastPromptTokens.Length, promptTokens.Length);
            while (common < limit && _lastPromptTokens[common] == promptTokens[common]) common++;
            // At least the final token is evaluated again so the logits are fresh.
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
                _logger.LogWarning("Prompt decode failed with {Result}. Clearing memory.", result);
                handle.MemoryClear(true);
                _lastPromptTokens = [];
                return string.Empty;
            }
            lastLogitsIndex = end - i - 1;
        }
        long prefillMs = sw.ElapsedMilliseconds;

        var buffer = new TokenHealingBuffer(healingPrefix);
        var sampledIds = new List<int>(options.MaxTokens + healingPrefix.Length);
        var scratch = new byte[512];
        string generatedText = string.Empty;
        int tokensGenerated = 0;
        int replayTokens = 0;
        string stopReason = "budget_exhausted";

        for (int step = 0; step < options.MaxTokens + healingPrefix.Length; step++)
        {
            if (ct.IsCancellationRequested) { stopReason = "cancelled"; break; }

            // The previously sampled token enters memory only when another token is wanted.
            if (sampledIds.Count > 0)
            {
                batch.Clear();
                batch.Add((LLamaToken)sampledIds[^1], promptTokens.Length + sampledIds.Count - 1, seq, logits: true);
                if (handle.Decode(batch) != DecodeResult.Ok) { stopReason = "accept_failed"; break; }
                lastLogitsIndex = 0;
            }

            var logits = handle.GetLogitsIth(lastLogitsIndex);
            LLamaToken? token;
            if (buffer.ReplayComplete)
            {
                token = SelectToken(
                    logits, options.TopK, RepetitionGuard.BlockedTokens(sampledIds, NoRepeatNgramSize),
                    forceWordContinuation: options.ForceWordContinuation && step == 0, options.SingleLine, scratch);
            }
            else
            {
                // While the healed prefix replays, only tokens that reproduce its bytes compete.
                token = SelectHealingToken(logits, buffer.RemainingReplay, options.SingleLine);
            }
            if (token is null) { stopReason = "no_admissible_token"; break; }

            var chosen = token.Value;
            if (chosen.IsEndOfGeneration(Vocab)) { stopReason = "eos"; break; }

            var piece = scratch.AsSpan(0, WritePiece(chosen, scratch));
            bool replayWasComplete = buffer.ReplayComplete;
            buffer.Append(piece);
            if (buffer.HasReplayMismatch)
            {
                generatedText = string.Empty;
                stopReason = "healing_prefix_mismatch";
                break;
            }
            sampledIds.Add((int)chosen);
            if (!replayWasComplete && !buffer.HasVisibleBytes)
            {
                replayTokens++;
                continue;
            }
            generatedText = buffer.Text;
            tokensGenerated++;

            if (DecodeStopPolicy.Verdict(
                    generatedText, tokensGenerated,
                    options.SentenceStopMinimumTokens, options.SentenceStopMinimumWords) is { } earlyStop)
            {
                stopReason = earlyStop.LogValue();
                break;
            }
            if (tokensGenerated >= options.MaxTokens) break;
        }

        // Only the prompt stays in memory so the next request can reuse it.
        if (_canReusePrefix)
        {
            handle.MemorySequenceRemove(seq, promptTokens.Length, -1);
            _lastPromptTokens = promptTokens;
        }
        else
        {
            _lastPromptTokens = [];
        }

        _logger.LogInformation(
            "Constrained decode: {Tokens} tokens (+{Replay} replayed) in {Ms} ms (prefill {Prefill} ms, reuse {Reuse}/{Total}), stop={Stop}.",
            tokensGenerated, replayTokens, sw.ElapsedMilliseconds, prefillMs, evalFrom, promptTokens.Length, stopReason);
        return generatedText;
    }

    /// <summary>
    /// Deterministic constrained selection. The candidate pool is the top-K token ids by raw logit.
    /// The walk skips control and blocked tokens and, on a forced mid-word first step, tokens that
    /// start a new word. The highest-logit survivor wins. An end-of-generation token is admissible,
    /// so the caller's stop check fires when the model's best choice is to stop.
    /// </summary>
    private LLamaToken? SelectToken(
        ReadOnlySpan<float> logits, int topK, HashSet<int> blocked,
        bool forceWordContinuation, bool singleLine, byte[] scratch)
    {
        if (topK <= 0) return null;
        int k = Math.Min(topK, logits.Length);

        // Partial top-K selection with fixed arrays and a single pass over the vocabulary.
        Span<int> ids = stackalloc int[64];
        Span<float> vals = stackalloc float[64];
        k = Math.Min(k, 64);
        int count = 0;
        for (int i = 0; i < logits.Length; i++)
        {
            float v = logits[i];
            if (count == k && v <= vals[count - 1]) continue;
            // Insertion into the descending top-K window.
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
            if (token.IsEndOfGeneration(Vocab)) return token;
            if (blocked.Contains(ids[i])) continue;

            int byteLen = WritePiece(token, scratch);
            if (byteLen == 0) continue;

            var piece = scratch.AsSpan(0, byteLen);
            // A single-line field cannot take a line break, so the next best token continues the line.
            if (singleLine && piece.IndexOf((byte)'\n') >= 0) continue;
            if (forceWordContinuation && !ContinuesWordMidStream(piece)) continue;
            return token;
        }
        return null;
    }

    /// <summary>The highest-logit token that can reproduce the remaining healed prefix, or null.</summary>
    private LLamaToken? SelectHealingToken(ReadOnlySpan<float> logits, ReadOnlySpan<byte> remainingReplay, bool singleLine)
    {
        LLamaToken? best = null;
        float bestLogit = float.NegativeInfinity;
        foreach (int id in _healingVocabulary.MatchingTokens(remainingReplay, singleLine))
        {
            if (best is null || logits[id] > bestLogit)
            {
                best = (LLamaToken)id;
                bestLogit = logits[id];
            }
        }
        return best;
    }

    /// <summary>
    /// Writes the printable bytes of <paramref name="token"/> into <paramref name="dest"/> and
    /// returns their count. Control, end-of-generation, unknown and unused tokens have none.
    /// </summary>
    private int WritePiece(LLamaToken token, Span<byte> dest)
    {
        if (token.IsControl(Vocab) || token.IsEndOfGeneration(Vocab)
            || (token.GetAttributes(Vocab) & (LLamaTokenAttr.Unknown | LLamaTokenAttr.Unused)) != 0)
        {
            return 0;
        }
        uint written = _weights.NativeHandle.TokenToSpan(token, dest, 0, false);
        return written > dest.Length ? 0 : (int)written;
    }

    private byte[] TokenPiece(int token)
    {
        Span<byte> scratch = stackalloc byte[512];
        return scratch[..WritePiece((LLamaToken)token, scratch)].ToArray();
    }

    /// <summary>
    /// Indexes every printable vocabulary piece. Chat-template markers are left out so a healed
    /// prefix can never be completed by scaffolding.
    /// </summary>
    private TokenHealingVocabulary BuildHealingVocabulary()
    {
        var entries = new List<(int Token, byte[] Piece)>(Vocab.Count);
        for (int token = 0; token < Vocab.Count; token++)
        {
            byte[] piece = TokenPiece(token);
            if (piece.Length == 0) continue;
            if (piece[0] is (byte)'<' or (byte)'[' && IsScaffoldingMarker(Encoding.UTF8.GetString(piece))) continue;
            entries.Add((token, piece));
        }
        return new TokenHealingVocabulary(entries);
    }

    private static bool IsScaffoldingMarker(string piece) =>
        ControlTokenMarkers.OpeningMarkers.Contains(piece) || ControlTokenMarkers.StopMarkers.Contains(piece);

    /// <summary>
    /// The first byte continues the current word. A letter, digit, apostrophe, hyphen or a
    /// non-ASCII lead byte qualifies.
    /// </summary>
    private static bool ContinuesWordMidStream(ReadOnlySpan<byte> bytes)
    {
        byte b = bytes[0];
        return b is (>= (byte)'a' and <= (byte)'z') or (>= (byte)'A' and <= (byte)'Z')
            or (>= (byte)'0' and <= (byte)'9') or (byte)'\'' or (byte)'-'
            || b >= 0x80;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _context.Dispose();
        _gate.Dispose();
    }
}
