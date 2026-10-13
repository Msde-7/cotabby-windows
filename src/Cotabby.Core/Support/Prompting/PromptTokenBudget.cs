namespace Cotabby.Core.Support;

/// <summary>
/// The estimated-token ceiling for the llama prompt (preface plus prefix), derived from the context
/// window so the renderer truncates against what the model can actually hold.
/// </summary>
public static class PromptTokenBudget
{
    /// <summary>
    /// Output reserve built into the configured prompt budget. It covers the default single-line
    /// request with room to spare.
    /// </summary>
    public const int OutputCeilingTokens = 50;

    /// <summary>Margin for the beginning-of-sequence token plus token-estimator error.</summary>
    public const int SafetyMarginTokens = 64;

    /// <summary>The context window minus the output ceiling and the safety margin.</summary>
    public static int Configured(int contextWindowTokens) =>
        contextWindowTokens - OutputCeilingTokens - SafetyMarginTokens;

    /// <summary>
    /// The prompt's token budget for one request. The configured budget holds back
    /// <see cref="OutputCeilingTokens"/> for output, but a request can ask for more. The runtime
    /// reserves the whole decode before admitting the prompt, so an overflowing prompt would lose its
    /// oldest raw tokens there and the preface would be cut mid-section. Taking the excess out of the
    /// budget here lets the renderer trim by section priority instead.
    /// </summary>
    public static int ForRequest(int configuredBudget, int maxPredictionTokens)
    {
        int excess = Math.Max(0, maxPredictionTokens - OutputCeilingTokens);
        return Math.Max(0, configuredBudget - excess);
    }
}
