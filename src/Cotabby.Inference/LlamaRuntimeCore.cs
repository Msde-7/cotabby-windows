using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
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

    /// <summary>
    /// Receives the text generated so far after each token, on the decode thread. If it throws,
    /// the exception is logged, the decode carries on and the callback is not called again.
    /// </summary>
    public Action<string>? OnPartial { get; init; }

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
/// thread-safe. A cancelled call, or disposal, aborts the native decode in flight through
/// llama.cpp's abort callback where the backend honours it (the CPU backend does), and otherwise
/// between prompt chunks and sampled tokens.
/// </remarks>
public sealed class LlamaRuntimeCore : IDisposable
{
    private const int NoRepeatNgramSize = 3;

    /// <summary>Argmax candidate pool size.</summary>
    private const int TopK = 20;

    /// <summary>Minimum tokens generated before a sentence end may stop decoding.</summary>
    private const int SentenceStopMinimumTokens = 2;

    /// <summary>
    /// Prewarm evaluates in chunks this small so a cancelled prewarm, and the real request
    /// waiting behind it, only wait for one short decode.
    /// </summary>
    private const int PrewarmChunkTokens = 64;

    /// <summary>The single sequence every prompt and decode lives in.</summary>
    private static readonly LLamaSeqId Sequence = (LLamaSeqId)0;

    private readonly LLamaWeights _weights;
    private readonly LLamaContext _context;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly bool _canReusePrefix;
    private readonly int _slidingWindow;
    private readonly TokenHealingVocabulary _healingVocabulary;

    /// <summary>Cancelled by <see cref="Dispose"/>. Every call links to it.</summary>
    private readonly CancellationTokenSource _lifetime = new();

    /// <summary>Registered with llama.cpp. Held here so the delegate lives as long as the context.</summary>
    private readonly AbortCallback _abortCallback;

    /// <summary>The token of the call holding the gate, read by the abort callback.</summary>
    private CancellationToken _abortToken;

    private LLamaToken[] _lastPromptTokens = [];
    private volatile bool _disposed;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.U1)]
    private delegate bool AbortCallback(IntPtr data);

    public LlamaRuntimeCore(LLamaWeights weights, LLamaContext context, ILogger logger)
    {
        _weights = weights;
        _context = context;
        _logger = logger;
        // Recurrent and hybrid models cannot drop a suffix of their state, so prefix reuse is
        // limited to models whose memory can shift.
        _canReusePrefix = !weights.NativeHandle.IsRecurrent && context.NativeHandle.MemoryCanShift;
        _slidingWindow = weights.NativeHandle.SWACount;
        _healingVocabulary = BuildHealingVocabulary();
        _abortCallback = _ => _abortToken.IsCancellationRequested;
        NativeApi.llama_set_abort_callback(
            context.NativeHandle, Marshal.GetFunctionPointerForDelegate(_abortCallback), IntPtr.Zero);
    }

    public SafeLlamaModelHandle.Vocabulary Vocab => _weights.Vocab;

    /// <summary>Size of the context window in tokens.</summary>
    public int ContextWindowTokens => (int)_context.NativeHandle.ContextSize;

    /// <summary>
    /// Run one constrained generation that continues <paramref name="prompt"/>. The prompt's final
    /// word is healed, so the model may re-spell the typed fragment with a token that also carries
    /// its continuation. Returns raw text, empty when nothing admissible was produced.
    /// </summary>
    public Task<string> GenerateAsync(string prompt, LlamaGenerationOptions options, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return RunExclusiveAsync(
            token =>
            {
                var (promptTokens, healingPrefix) = PreparePrompt(prompt, options);
                return Generate(promptTokens, healingPrefix, options, token);
            },
            whenDisposed: string.Empty,
            ct);
    }

    /// <summary>
    /// Evaluates <paramref name="prompt"/> into memory without sampling, reusing the prefix
    /// already there exactly as a generation does, so a later <see cref="GenerateAsync"/> with the
    /// same <paramref name="options"/> and the same head only evaluates the rest.
    /// </summary>
    /// <remarks>
    /// Best effort and low priority. It evaluates in small chunks and checks
    /// <paramref name="ct"/> between them, so a cancelled prewarm gives up the gate within one
    /// chunk. Whatever was evaluated before the cancellation stays in memory for reuse. It
    /// completes normally when cancelled. A model that cannot keep a prefix has nothing to warm.
    /// </remarks>
    public async Task PrewarmAsync(string prompt, LlamaGenerationOptions options, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_canReusePrefix || ct.IsCancellationRequested) return;
        try
        {
            await RunExclusiveAsync(
                token =>
                {
                    var (promptTokens, _) = PreparePrompt(prompt, options);
                    Prewarm(promptTokens, token);
                    return true;
                },
                whenDisposed: false,
                ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    /// <summary>
    /// Runs <paramref name="work"/> on the thread pool while holding the gate. Everything that
    /// touches the context, tokenizing included, runs inside, after the disposal check, so an
    /// unload can never free the context under it.
    /// </summary>
    /// <remarks>
    /// <paramref name="work"/> receives <paramref name="ct"/> linked to disposal, and the native
    /// abort callback watches that token while it runs, so an unload ends the call quickly. A
    /// call that only disposal cancelled returns <paramref name="whenDisposed"/>.
    /// </remarks>
    private async Task<T> RunExclusiveAsync<T>(Func<CancellationToken, T> work, T whenDisposed, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        try
        {
            await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return whenDisposed;
        }
        try
        {
            // The model can be unloaded while this request waited for its turn.
            if (_disposed) return whenDisposed;
            return await Task.Run(
                () =>
                {
                    _abortToken = linked.Token;
                    try
                    {
                        return work(linked.Token);
                    }
                    finally
                    {
                        _abortToken = CancellationToken.None;
                    }
                },
                CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Tokenizes and heals the prompt, then fits it beside the whole decode. A byte-fallback
    /// vocabulary can replay one token per healed byte, so the replay has its own allowance and
    /// cannot consume the output budget. Must run under the gate.
    /// </summary>
    private (LLamaToken[] Tokens, byte[] HealingPrefix) PreparePrompt(string prompt, LlamaGenerationOptions options)
    {
        var handle = _context.NativeHandle;
        var tokens = handle.Tokenize(prompt, add_bos: Vocab.ShouldAddBOS, special: false, Encoding.UTF8);
        var healing = new TokenHealingPlan(
            prompt, Array.ConvertAll(tokens, token => (int)token), options.SingleLine, TokenPiece);
        var promptTokens = healing.PromptTokens.Select(token => (LLamaToken)token).ToArray();

        int maxPromptTokens = Math.Max(1, (int)handle.ContextSize - options.MaxTokens - healing.ReplayBytes.Length);
        bool startsWithBos = promptTokens.Length > 0 && Vocab.BOS is { } bos && promptTokens[0] == bos;
        return (PromptWindow.Fit(promptTokens, maxPromptTokens, startsWithBos), healing.ReplayBytes);
    }

    private void Prewarm(LLamaToken[] promptTokens, CancellationToken ct)
    {
        if (promptTokens.Length == 0) return;
        var sw = Stopwatch.StartNew();
        try
        {
            int chunk = Math.Min((int)_context.NativeHandle.BatchSize, PrewarmChunkTokens);
            var outcome = EvaluatePrompt(promptTokens, refreshLastToken: false, chunk, ct, out int reused, out _);
            _logger.LogDebug(
                "Prompt prewarm: {Outcome} in {Ms} ms (reuse {Reuse}/{Total}).",
                outcome, sw.ElapsedMilliseconds, reused, promptTokens.Length);
        }
        catch
        {
            ForgetPromptAfterFailure();
            throw;
        }
    }

    private enum PromptEvaluation
    {
        Ready,
        Cancelled,
        Failed,
    }

    /// <summary>
    /// Brings memory to hold exactly <paramref name="promptTokens"/>. The longest common token
    /// prefix with the remembered prompt stays in memory and only the divergent tail is evaluated.
    /// A model that cannot shift, or a memory that refuses the cut, is cleared and evaluated fully.
    /// </summary>
    /// <remarks>
    /// <see cref="_lastPromptTokens"/> follows memory after every chunk, so a cancelled evaluation
    /// leaves a valid prefix that the next request reuses instead of starting over.
    /// </remarks>
    /// <param name="refreshLastToken">
    /// Evaluate the final token again even when it is already in memory, because a generation
    /// needs its logits.
    /// </param>
    private PromptEvaluation EvaluatePrompt(
        LLamaToken[] promptTokens, bool refreshLastToken, int chunkSize, CancellationToken ct,
        out int reused, out int lastLogitsIndex)
    {
        var handle = _context.NativeHandle;
        int evalFrom = 0;
        if (_canReusePrefix)
        {
            int limit = Math.Min(_lastPromptTokens.Length, promptTokens.Length);
            while (evalFrom < limit && _lastPromptTokens[evalFrom] == promptTokens[evalFrom]) evalFrom++;
            if (refreshLastToken) evalFrom = Math.Min(evalFrom, promptTokens.Length - 1);
        }
        if (evalFrom > 0 && !TryTrimMemory(evalFrom))
        {
            // Decoding the tail onto state that still holds the old tokens would corrupt it.
            _logger.LogDebug("Memory refused to drop the tail after token {From}. Evaluating the prompt fully.", evalFrom);
            evalFrom = 0;
        }
        if (evalFrom == 0) handle.MemoryClear(true);
        _lastPromptTokens = promptTokens[..evalFrom];

        reused = evalFrom;
        lastLogitsIndex = 0;
        var batch = new LLamaBatch();
        for (int i = evalFrom; i < promptTokens.Length; i += chunkSize)
        {
            if (ct.IsCancellationRequested) return PromptEvaluation.Cancelled;
            batch.Clear();
            int end = Math.Min(promptTokens.Length, i + chunkSize);
            for (int j = i; j < end; j++)
            {
                batch.Add(promptTokens[j], j, Sequence, logits: j == promptTokens.Length - 1);
            }
            var result = handle.Decode(batch);
            if (result == DecodeResult.ComputeAborted && ct.IsCancellationRequested)
            {
                KeepPrefixAfterAbort(promptTokens, end);
                return PromptEvaluation.Cancelled;
            }
            if (result != DecodeResult.Ok)
            {
                _logger.LogWarning("Prompt decode failed with {Result}. Clearing memory.", result);
                ForgetPrompt();
                return PromptEvaluation.Failed;
            }
            _lastPromptTokens = promptTokens[..end];
            lastLogitsIndex = end - i - 1;
        }
        return PromptEvaluation.Ready;
    }

    /// <summary>
    /// Drops every position from <paramref name="from"/> on and reports whether memory now holds
    /// exactly the positions before it. LLamaSharp's <c>MemorySequenceRemove</c> discards the
    /// result of <c>llama_memory_seq_rm</c>, which is false when a recurrent or hybrid memory
    /// refuses a partial removal and leaves its state untouched, so the remaining positions are
    /// the check. Memory that lost more than the tail fails it too.
    /// </summary>
    private bool TryTrimMemory(int from)
    {
        _context.NativeHandle.MemorySequenceRemove(Sequence, from, -1);
        return HoldsPrefix(from);
    }

    /// <summary>See <see cref="MemoryPrefix.HoldsExactly"/>.</summary>
    private bool HoldsPrefix(int length)
    {
        var handle = _context.NativeHandle;
        int max = handle.MemorySequenceMaxPosition(Sequence).Value;
        if (max != length - 1) return false;
        return MemoryPrefix.HoldsExactly(length, handle.MemorySequenceMinPosition(Sequence).Value, max, _slidingWindow);
    }

    /// <summary>
    /// Brings the remembered prompt back in line with memory after the abort callback stopped the
    /// prompt chunk ending at <paramref name="end"/>. llama.cpp keeps the ubatches it finished and
    /// removes the one it was computing. A memory that cannot remove it, such as a hybrid model's,
    /// still reports the whole chunk although its state is half computed, so anything other than a
    /// clean prefix that stops before <paramref name="end"/> is forgotten.
    /// </summary>
    private void KeepPrefixAfterAbort(LLamaToken[] promptTokens, int end)
    {
        int kept = _context.NativeHandle.MemorySequenceMaxPosition(Sequence).Value + 1;
        if (_canReusePrefix && kept < end && HoldsPrefix(kept))
        {
            _lastPromptTokens = promptTokens[..kept];
        }
        else
        {
            ForgetPrompt();
        }
    }

    private string Generate(
        LLamaToken[] promptTokens, byte[] healingPrefix, LlamaGenerationOptions options, CancellationToken ct)
    {
        if (promptTokens.Length == 0) return string.Empty;
        try
        {
            return Decode(promptTokens, healingPrefix, options, ct);
        }
        catch
        {
            // Memory may now hold sampled or half-evaluated tokens that the remembered prompt
            // does not describe, and reusing it would decode onto the wrong state.
            ForgetPromptAfterFailure();
            throw;
        }
    }

    private string Decode(
        LLamaToken[] promptTokens, byte[] healingPrefix, LlamaGenerationOptions options, CancellationToken ct)
    {
        var handle = _context.NativeHandle;
        var sw = Stopwatch.StartNew();

        var evaluation = EvaluatePrompt(
            promptTokens, refreshLastToken: true, (int)handle.BatchSize, ct, out int evalFrom, out int lastLogitsIndex);
        if (evaluation != PromptEvaluation.Ready) return string.Empty;
        long prefillMs = sw.ElapsedMilliseconds;

        var batch = new LLamaBatch();
        var buffer = new TokenHealingBuffer(healingPrefix);
        var sampledIds = new List<int>(options.MaxTokens + healingPrefix.Length);
        var scratch = new byte[512];
        var onPartial = options.OnPartial;
        string generatedText = string.Empty;
        int tokensGenerated = 0;
        int replayTokens = 0;
        string stopReason = "budget_exhausted";

        for (int step = 0; step < options.MaxTokens + healingPrefix.Length; step++)
        {
            if (ct.IsCancellationRequested) { stopReason = "cancelled"; break; }

            // The last sampled token enters memory only when another token is wanted.
            if (sampledIds.Count > 0)
            {
                batch.Clear();
                batch.Add((LLamaToken)sampledIds[^1], promptTokens.Length + sampledIds.Count - 1, Sequence, logits: true);
                var accepted = handle.Decode(batch);
                if (accepted != DecodeResult.Ok)
                {
                    stopReason = accepted == DecodeResult.ComputeAborted && ct.IsCancellationRequested
                        ? "cancelled"
                        : "accept_failed";
                    break;
                }
                lastLogitsIndex = 0;
            }

            var logits = handle.GetLogitsIth(lastLogitsIndex);
            LLamaToken? token;
            if (buffer.ReplayComplete)
            {
                token = SelectToken(
                    logits, RepetitionGuard.BlockedTokens(sampledIds, NoRepeatNgramSize), options.SingleLine, scratch);
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
            if (onPartial is not null)
            {
                // The callback is the caller's code running on the decode thread. Its failure must
                // not abort the decode or skip the memory bookkeeping below.
                try
                {
                    onPartial(generatedText);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Partial result callback failed. No further partials for this decode.");
                    onPartial = null;
                }
            }

            if (DecodeStopPolicy.Verdict(
                    generatedText, tokensGenerated,
                    SentenceStopMinimumTokens, options.SentenceStopMinimumWords) is { } earlyStop)
            {
                stopReason = earlyStop.LogValue();
                break;
            }
            if (tokensGenerated >= options.MaxTokens) break;
        }

        // Only the prompt stays in memory so the next request can reuse it. A memory that refuses
        // to drop the sampled tokens is forgotten, so the next request clears it.
        _lastPromptTokens = _canReusePrefix && TryTrimMemory(promptTokens.Length) ? promptTokens : [];

        _logger.LogInformation(
            "Constrained decode: {Tokens} tokens (+{Replay} replayed) in {Ms} ms (prefill {Prefill} ms, reuse {Reuse}/{Total}), stop={Stop}.",
            tokensGenerated, replayTokens, sw.ElapsedMilliseconds, prefillMs, evalFrom, promptTokens.Length, stopReason);
        return generatedText;
    }

    /// <summary>
    /// Deterministic constrained selection. The candidate pool is the top-K token ids by raw logit.
    /// The walk skips control and blocked tokens, and the highest-logit survivor wins. An
    /// end-of-generation token is admissible, so the caller's stop check fires when the model's best
    /// choice is to stop.
    /// </summary>
    private LLamaToken? SelectToken(ReadOnlySpan<float> logits, HashSet<int> blocked, bool singleLine, byte[] scratch)
    {
        int k = Math.Min(TopK, logits.Length);

        // Partial top-K selection with fixed arrays and a single pass over the vocabulary.
        Span<int> ids = stackalloc int[TopK];
        Span<float> vals = stackalloc float[TopK];
        int count = 0;
        int width = Vector<float>.Count;
        int i = 0;
        while (i < logits.Length)
        {
            // Once the window is full, whole vectors at or below its lowest logit are skipped. They
            // are exactly the values the scalar test below would skip.
            if (count == k && Vector.IsHardwareAccelerated)
            {
                var floor = new Vector<float>(vals[k - 1]);
                while (i <= logits.Length - width && Vector.LessThanOrEqualAll(new Vector<float>(logits[i..]), floor)) i += width;
            }
            for (int end = Math.Min(logits.Length, i + width); i < end; i++)
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
        }

        for (int rank = 0; rank < count; rank++)
        {
            var token = (LLamaToken)ids[rank];
            if (token.IsEndOfGeneration(Vocab)) return token;
            if (blocked.Contains(ids[rank])) continue;

            int byteLen = WritePiece(token, scratch);
            if (byteLen == 0) continue;

            var piece = scratch.AsSpan(0, byteLen);
            // A single-line field cannot take a line break, so the next best token continues the line.
            if (singleLine && piece.IndexOf((byte)'\n') >= 0) continue;
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

    private void ForgetPrompt()
    {
        _lastPromptTokens = [];
        _context.NativeHandle.MemoryClear(true);
    }

    /// <summary>
    /// Forgets the prompt while an exception is already propagating. The remembered prompt is
    /// dropped first, so even if clearing fails the next request clears memory before reuse, and
    /// a clearing failure is logged rather than hiding the original exception.
    /// </summary>
    private void ForgetPromptAfterFailure()
    {
        try
        {
            ForgetPrompt();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Clearing memory after a failed decode also failed.");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Cancelling aborts the decode in flight and releases every call waiting for the gate,
        // so the wait below is short.
        _lifetime.Cancel();
        // A decode in flight reads the context, so it is freed only once that decode has ended.
        _gate.Wait();
        try
        {
            _context.Dispose();
        }
        finally
        {
            _gate.Release();
        }
    }
}
