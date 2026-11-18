using System.Text;

namespace Cotabby.Core.Support;

/// <summary>
/// Separates a healed prompt suffix's replay from the new text a completion is allowed to show.
/// The sampler may choose a larger token that begins with the exact bytes removed from the prompt.
/// Those bytes already belong to the editor and must never appear in ghost text. Matching is on
/// bytes, which also handles token pieces that split a Unicode scalar. After replay, incomplete
/// UTF-8 stays buffered until a complete string is available.
/// </summary>
public sealed class TokenHealingBuffer
{
    /// <summary>Bounds the extra sampling work independently of the visible completion's token budget.</summary>
    public const int MaximumReplayTokens = 16;

    /// <summary>
    /// A byte-fallback vocabulary can need one token per byte, so capping the healed bytes at the
    /// replay token count guarantees the replay fits even then.
    /// </summary>
    public const int MaximumHealedTokenBytes = MaximumReplayTokens;

    private readonly byte[] _replay;
    private int _replayed;
    private readonly List<byte> _visibleBytes = [];

    /// <summary>How many of <see cref="_visibleBytes"/> are complete, valid UTF-8.</summary>
    private int _validBytes;

    public TokenHealingBuffer(ReadOnlySpan<byte> replayedPrefix)
    {
        _replay = replayedPrefix.ToArray();
    }

    /// <summary>Latest complete, valid UTF-8 continuation, excluding every replayed byte.</summary>
    public string Text { get; private set; } = string.Empty;

    /// <summary>A mismatch permanently suppresses this decode, so the runtime can stop sampling.</summary>
    public bool HasReplayMismatch { get; private set; }

    public bool ReplayComplete => !HasReplayMismatch && _replayed == _replay.Length;

    /// <summary>The prompt bytes the sampler still has to reproduce.</summary>
    public ReadOnlySpan<byte> RemainingReplay => _replay.AsSpan(_replayed);

    /// <summary>
    /// True once a sampled token has added new bytes, even if they do not yet form a complete scalar.
    /// Such a token still consumes the output budget and is not mistaken for invisible replay.
    /// </summary>
    public bool HasVisibleBytes => _visibleBytes.Count > 0;

    /// <summary>
    /// The output reached bytes that are not UTF-8. <see cref="Text"/> keeps the valid text before
    /// them and never grows again, so the runtime can stop sampling.
    /// </summary>
    public bool HasInvalidBytes { get; private set; }

    /// <summary>
    /// Returns the cumulative visible text only when this piece adds a complete UTF-8 continuation.
    /// Null during replay or a split scalar.
    /// </summary>
    public string? Append(ReadOnlySpan<byte> tokenBytes)
    {
        if (HasReplayMismatch || HasInvalidBytes) return null;

        int replayCount = Math.Min(_replay.Length - _replayed, tokenBytes.Length);
        if (!RemainingReplay[..replayCount].SequenceEqual(tokenBytes[..replayCount]))
        {
            HasReplayMismatch = true;
            _visibleBytes.Clear();
            Text = string.Empty;
            return null;
        }
        _replayed += replayCount;
        if (_replayed < _replay.Length) return null;

        var newBytes = tokenBytes[replayCount..];
        if (newBytes.IsEmpty) return null;
        _visibleBytes.AddRange(newBytes);

        // Lossy decoding would turn each unfinished token piece into U+FFFD. The original bytes are
        // kept until later pieces complete the scalar, and an invalid sequence never publishes.
        // The complete scalars before an unfinished or invalid one are published, so one bad byte
        // does not freeze the text at the token before it.
        var visible = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_visibleBytes);
        int valid = _validBytes;
        while (valid < visible.Length)
        {
            var status = Rune.DecodeFromUtf8(visible[valid..], out _, out int consumed);
            if (status == System.Buffers.OperationStatus.Done)
            {
                valid += consumed;
                continue;
            }
            if (status == System.Buffers.OperationStatus.InvalidData) HasInvalidBytes = true;
            break;
        }
        if (valid == _validBytes) return null;
        _validBytes = valid;
        Text = Encoding.UTF8.GetString(visible[..valid]);
        return Text;
    }
}
