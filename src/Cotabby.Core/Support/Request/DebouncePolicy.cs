namespace Cotabby.Core.Support;

/// <summary>
/// Chooses the prediction debounce from the last observed generation latency. A fixed debounce adds
/// avoidable delay on fast hardware and lets keystrokes pile doomed generations onto a model that
/// cannot keep up on slow hardware. The configured value remains the fallback until a first latency
/// exists.
/// </summary>
public static class DebouncePolicy
{
    /// <param name="lastGenerationLatencyMilliseconds">Latency of the most recent generation, if any.</param>
    /// <param name="fallback">The configured debounce.</param>
    /// <param name="isRemoteEndpoint">True for an HTTP endpoint engine. It cannot reuse the in-process
    /// KV cache and cancelling does not stop the server at once, so a longer trailing-edge pause
    /// collapses a typing burst into one useful request.</param>
    public static int Milliseconds(int? lastGenerationLatencyMilliseconds, int fallback, bool isRemoteEndpoint = false)
    {
        int? last = lastGenerationLatencyMilliseconds is > 0 ? lastGenerationLatencyMilliseconds : null;
        if (isRemoteEndpoint)
        {
            return last switch
            {
                null => Math.Max(fallback, 180),
                <= 300 => 100,
                <= 700 => 150,
                _ => 220,
            };
        }
        return last switch
        {
            null => fallback,
            <= 70 => 15,
            <= 140 => 25,
            _ => 55,
        };
    }
}
