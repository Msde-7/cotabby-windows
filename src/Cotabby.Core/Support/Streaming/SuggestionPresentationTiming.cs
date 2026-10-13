namespace Cotabby.Core.Support;

/// <summary>
/// Measures the first accepted overlay presentation after the latest input event. The owner supplies
/// monotonic timestamps in seconds. No typed text is stored. This measures presentation submission,
/// not a rendered frame or whether the suggestion was useful.
/// </summary>
/// <typeparam name="TIdentity">Identity of one focused input observation.</typeparam>
public sealed class SuggestionPresentationTiming<TIdentity>
    where TIdentity : IEquatable<TIdentity>
{
    public readonly record struct Measurement(string InputKind, double Milliseconds);

    private (TIdentity Identity, string Kind, double Time)? _pending;

    public void Begin(TIdentity identity, string kind, double time)
    {
        _pending = double.IsFinite(time) ? (identity, kind, time) : null;
    }

    public void Clear() => _pending = null;

    /// <summary>The latency since the pending input, or null. The pending input is consumed either way.</summary>
    public Measurement? Presented(TIdentity identity, double time)
    {
        var pending = _pending;
        _pending = null;
        if (pending is not { } input || !input.Identity.Equals(identity) || !double.IsFinite(time) || time < input.Time)
        {
            return null;
        }
        return new Measurement(input.Kind, (time - input.Time) * 1_000);
    }
}
