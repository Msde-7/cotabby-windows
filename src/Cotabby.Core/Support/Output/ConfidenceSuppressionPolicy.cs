namespace Cotabby.Core.Support;

/// <summary>
/// Decides whether a completion is too low-confidence to show, from the model's own per-token
/// log-probabilities. A floor of negative infinity disables suppression.
/// </summary>
public static class ConfidenceSuppressionPolicy
{
    /// <summary>Suppress when the average per-token log-probability is below <paramref name="floor"/>.</summary>
    public static bool ShouldSuppress(double averageLogprob, double floor) =>
        floor > double.NegativeInfinity && averageLogprob < floor;
}
