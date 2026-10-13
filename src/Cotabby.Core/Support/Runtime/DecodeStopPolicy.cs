using Cotabby.Core.Text;

namespace Cotabby.Core.Support;

/// <summary>Why decoding stopped early.</summary>
public enum DecodeStopReason
{
    SentenceBoundary,
    ScaffoldingMarker,
}

/// <summary>
/// Decides, during decoding, whether the completion accumulated so far should stop generation early.
/// <list type="bullet">
/// <item>Sentence boundary. The text ends at a natural sentence end. A minimum-token guard avoids
/// degenerate instant stops such as a lone leading period.</item>
/// <item>Scaffolding marker. The text contains a chat-template stop marker. The normalizer truncates
/// at that marker anyway, so everything generated past it is discarded work. There is no minimum
/// here, because a marker means the model believes the turn is over however early it appears.</item>
/// </list>
/// Both checks inspect only the accumulated string, so they add no per-token vocabulary work.
/// </summary>
public static class DecodeStopPolicy
{
    public static DecodeStopReason? Verdict(
        string accumulated, int tokensGenerated, int minimumTokens = 2, int minimumWords = 0)
    {
        if (ContainsScaffoldingStopMarker(accumulated)) return DecodeStopReason.ScaffoldingMarker;
        if (tokensGenerated < minimumTokens) return null;

        // A sentence end before the preset's minimum word count ("report.") made one ghost a single
        // word and the next a whole phrase. The model carries on into the next sentence instead.
        if (minimumWords > 0 && GraphemeString.Words(accumulated).Count < minimumWords) return null;
        return SentenceBoundaryClassifier.EndsSentence(accumulated) ? DecodeStopReason.SentenceBoundary : null;
    }

    /// <summary>The value written to the decode log's stop reason field.</summary>
    public static string LogValue(this DecodeStopReason reason) =>
        reason == DecodeStopReason.SentenceBoundary ? "sentence_boundary" : "scaffolding_marker";

    private static bool ContainsScaffoldingStopMarker(string text)
    {
        // Every stop marker starts with "<", so most prose never reaches the per-marker scan.
        if (!text.Contains('<')) return false;
        foreach (string marker in ControlTokenMarkers.StopMarkers)
        {
            if (text.Contains(marker, StringComparison.Ordinal)) return true;
        }
        return false;
    }
}
