namespace Cotabby.Inference;

/// <summary>
/// Decides whether a sequence's memory, as llama.cpp reports it, holds exactly the start of a
/// prompt, so that only the rest needs evaluating.
/// </summary>
public static class MemoryPrefix
{
    /// <summary>
    /// True when memory spanning <paramref name="minPosition"/> to <paramref name="maxPosition"/>
    /// holds exactly the first <paramref name="length"/> prompt tokens and enough of them to decode
    /// the next one.
    /// </summary>
    /// <remarks>
    /// llama.cpp guarantees every position between the reported minimum and maximum is present.
    /// Memory that ends before the cut dropped more than the tail, as a hybrid model's recurrent
    /// state does when a removal invalidates it, so the next token would be decoded onto missing
    /// state. A sliding-window cache may have evicted positions the window no longer reaches, but
    /// it must still reach back as far as llama.cpp's server requires before reusing it.
    /// </remarks>
    /// <param name="slidingWindow">The model's sliding attention window, 0 when it has none.</param>
    public static bool HoldsExactly(int length, int minPosition, int maxPosition, int slidingWindow)
    {
        if (length <= 0) return maxPosition < 0;
        if (maxPosition != length - 1) return false;
        int oldestNeeded = slidingWindow > 0 ? Math.Max(0, length - slidingWindow) : 0;
        return minPosition <= oldestNeeded;
    }
}
