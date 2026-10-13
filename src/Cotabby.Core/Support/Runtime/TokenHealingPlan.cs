using System.Text;
using Cotabby.Core.Text;

namespace Cotabby.Core.Support;

/// <summary>
/// Chooses the bounded prompt suffix that sampling may retokenize at the caret. A word can span
/// several tokens, so undoing only the final token can strand the model on a spelling such as
/// " int" + "ell" instead of allowing its vocabulary token for " intelligence". This plan backs up
/// to the current word when it fits the replay allowance, always preserving exact editor bytes.
/// <see cref="TokenHealingBuffer"/> later removes those bytes from the visible completion.
/// </summary>
public sealed class TokenHealingPlan
{
    public IReadOnlyList<int> PromptTokens { get; }

    public byte[] ReplayBytes { get; }

    private TokenHealingPlan(IReadOnlyList<int> promptTokens, byte[] replayBytes)
    {
        PromptTokens = promptTokens;
        ReplayBytes = replayBytes;
    }

    /// <summary>
    /// The prompt exactly as tokenized, replaying nothing. Mid-word insertions use this so the
    /// new-word mask still constrains their first sampled token.
    /// </summary>
    public static TokenHealingPlan Unhealed(IReadOnlyList<int> tokens) => new(tokens, []);

    /// <param name="prompt">The prompt text the tokens encode.</param>
    /// <param name="tokens">The prompt's tokens.</param>
    /// <param name="singleLine">When true, a line break is never moved into the replay.</param>
    /// <param name="piece">Decoded bytes of one token. Empty for special tokens.</param>
    public TokenHealingPlan(string prompt, IReadOnlyList<int> tokens, bool singleLine, Func<int, byte[]> piece)
    {
        byte[] promptBytes = Encoding.UTF8.GetBytes(prompt);
        int keptCount = tokens.Count;
        byte[] replay = [];

        bool IsReplayable(byte[] candidate) =>
            candidate.Length <= TokenHealingBuffer.MaximumHealedTokenBytes
            && promptBytes.AsSpan().EndsWith(candidate)
            && !(singleLine && candidate.AsSpan().IndexOfAny((byte)'\n', (byte)'\r') >= 0);

        // At least one conditioning token is retained. Empty or special pieces and tokenizer
        // normalization must never remove text that cannot be replayed byte for byte.
        if (keptCount > 1)
        {
            byte[] candidate = piece(tokens[keptCount - 1]);
            if (candidate.Length > 0 && IsReplayable(candidate))
            {
                keptCount--;
                replay = candidate;
            }
        }

        if (replay.Length > 0 && GraphemeString.LastOf(prompt) is { IsLetterOrNumber: true })
        {
            int needed = CurrentWordByteCount(prompt);
            while (needed <= TokenHealingBuffer.MaximumHealedTokenBytes && replay.Length < needed && keptCount > 1)
            {
                byte[] preceding = piece(tokens[keptCount - 1]);
                byte[] candidate = [.. preceding, .. replay];
                if (preceding.Length == 0 || !IsReplayable(candidate)) break;
                keptCount--;
                replay = candidate;
            }
        }

        PromptTokens = keptCount == tokens.Count ? tokens : tokens.Take(keptCount).ToArray();
        ReplayBytes = replay;
    }

    /// <summary>
    /// UTF-8 length of the unfinished word ending the prompt, including one horizontal separator
    /// before it because vocabularies commonly encode " word" as one token. A newline stays in the
    /// conditioning context. Returns more than the replay allowance when the word does not fit.
    /// </summary>
    private static int CurrentWordByteCount(string prompt)
    {
        int bytes = 0;
        int end = prompt.Length;
        while (end > 0 && bytes <= TokenHealingBuffer.MaximumHealedTokenBytes)
        {
            Rune.DecodeLastFromUtf16(prompt.AsSpan(0, end), out var scalar, out int consumed);
            if (Rune.IsWhiteSpace(scalar))
            {
                return ScalarSets.IsNewline(scalar) ? bytes : bytes + scalar.Utf8SequenceLength;
            }
            bytes += scalar.Utf8SequenceLength;
            end -= consumed;
        }
        return bytes;
    }
}
