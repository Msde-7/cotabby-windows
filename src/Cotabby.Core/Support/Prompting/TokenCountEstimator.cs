using Cotabby.Core.Text;

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
        var graphemes = new GraphemeString(text);
        int total = 0;
        int word = 0;
        for (int index = 0; index < graphemes.Count; index++)
        {
            var grapheme = graphemes[index];
            if (grapheme.IsWhitespace || grapheme.IsPunctuation)
            {
                total += WordTokens(word);
                word = 0;
            }
            else
            {
                word++;
            }
        }
        return total + WordTokens(word);
    }

    private static int WordTokens(int graphemes) =>
        graphemes == 0 ? 0 : Math.Max(1, (int)Math.Round(graphemes / 4.0, MidpointRounding.AwayFromZero));
}
