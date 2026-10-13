using System.Text;
using System.Text.Unicode;

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
    /// Returns the cumulative visible text only when this piece adds a complete UTF-8 continuation.
    /// Null during replay or a split scalar.
    /// </summary>
    public string? Append(ReadOnlySpan<byte> tokenBytes)
    {
        if (HasReplayMismatch) return null;

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
        var visible = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_visibleBytes);
        if (!Utf8.IsValid(visible)) return null;
        Text = Encoding.UTF8.GetString(visible);
        return Text;
    }
}
