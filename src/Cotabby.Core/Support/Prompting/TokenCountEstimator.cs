namespace Cotabby.Core.Support;

/// <summary>
/// A cheap estimate of how many model tokens a string occupies, used to budget the base-model prompt
/// more faithfully than a flat character count. It is a word-aware heuristic (about four characters
/// per token within a word, every word at least one token), so it is used only for relative budgeting
/// and never to assert a hard token limit.
/// </summary>
public static class TokenCountEstimator
{
    public static int Estimate(string text)
    {
        // Split on punctuation as well as whitespace. Real subword tokenizers break "can't", "end."
        // and "func()" into multiple tokens, so gluing punctuation to a word would undercount code.
        int total = 0;
        foreach (var word in new GraphemeString(text).Split(g => g.IsWhitespace || g.IsPunctuation))
        {
            int length = GraphemeString.CountOf(text[word]);
            total += Math.Max(1, (int)Math.Round(length / 4.0, MidpointRounding.AwayFromZero));
        }
        return total;
    }
}
