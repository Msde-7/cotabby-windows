namespace Cotabby.Inference;

/// <summary>
/// Fits a tokenized prompt into the room the context leaves for it, so that consecutive
/// keystrokes still share a long token prefix with the prompt already in memory.
/// </summary>
/// <remarks>
/// Cutting exactly the overflow from the front would shift the kept window by a token on every
/// keystroke. The kept head would then never match the previous prompt, every request would
/// evaluate the whole window again, and the beginning-of-sequence token the model was trained
/// with would be gone. Instead the first token is kept when it is BOS and the front is dropped in
/// coarse steps, so the kept head only moves once the prompt has grown by a whole step.
/// </remarks>
public static class PromptWindow
{
    /// <summary>The largest number of tokens dropped from the front at a time.</summary>
    public const int CutStep = 256;

    /// <summary>
    /// The tokens to evaluate. <paramref name="tokens"/> itself when it fits in
    /// <paramref name="maxTokens"/>. Otherwise the tail, behind the first token when
    /// <paramref name="keepFirst"/> says it is a BOS token, cut at a multiple of the step.
    /// </summary>
    public static T[] Fit<T>(T[] tokens, int maxTokens, bool keepFirst)
    {
        if (tokens.Length <= maxTokens) return tokens;
        if (maxTokens <= 0) return [];

        int head = keepFirst && maxTokens > 1 ? 1 : 0;
        int body = tokens.Length - head;
        int room = maxTokens - head;
        int excess = body - room;
        // A small window loses at most a quarter of itself to the rounding.
        int step = Math.Clamp(room / 4, 1, CutStep);
        int cut = (excess + step - 1) / step * step;
        return head == 0 ? tokens[cut..] : [tokens[0], .. tokens.AsSpan(head + cut)];
    }
}
