using System.Text;
using Cotabby.Core.Text;

namespace Cotabby.Core.Support;

/// <summary>
/// Recognizes a caret that sits right after a valediction ("Best,", or "Thanks again," on its own
/// line), the one place in a text where the writer's name is what comes next. A base model given a
/// name in every preface reaches for it whenever the caret text is thin, so
/// <see cref="BaseCompletionPromptRenderer"/> renders the persona line only here.
/// </summary>
public static class SignOffCue
{
    /// <summary>
    /// Closing lines a writer follows with their name. Lower case, punctuation stripped, matched whole
    /// so "I wish you the best" or an already signed "thanks, Sam" never qualifies.
    /// </summary>
    public static readonly IReadOnlySet<string> Valedictions = new HashSet<string>(StringComparer.Ordinal)
    {
        "best", "all the best", "best regards", "best wishes", "with best wishes",
        "kind regards", "kindest regards", "warm regards", "warmest regards", "regards",
        "warmly", "warmest", "sincerely", "sincerely yours", "yours sincerely", "yours truly",
        "yours", "yours faithfully", "faithfully", "truly",
        "cheers", "thanks", "thank you", "thanks again", "thanks so much", "thank you so much",
        "thanks a lot", "thanks a bunch", "many thanks", "much appreciated", "with thanks",
        "with gratitude", "with appreciation", "gratefully", "in gratitude",
        "take care", "talk soon", "speak soon", "see you soon", "until next time",
        "respectfully", "respectfully yours", "cordially", "cordially yours",
        "love", "with love", "much love", "lots of love", "hugs", "xoxo", "xo",
        "peace", "blessings", "god bless",
    };

    private static readonly char[] SentenceTerminators = ['.', '!', '?'];
    private const string EdgePunctuation = ",.!:;-–—~*_\"'()";

    /// <summary>
    /// True when <paramref name="prefix"/> ends in a valediction, so the caret (after its comma, or on
    /// the next line) is where the writer signs.
    /// </summary>
    public static bool PrecedesSignature(string prefix)
    {
        // The closing usually sits on its own line. It is also found at the end of the last
        // sentence, for hosts that run lines together ("See you Friday. Thanks,").
        if (LastNonBlankLine(prefix) is { } line && Valedictions.Contains(Normalized(line))) return true;
        string body = ScalarSets.Trim(prefix, IsEdge);
        int terminator = body.LastIndexOfAny(SentenceTerminators);
        return terminator >= 0 && Valedictions.Contains(Normalized(body[(terminator + 1)..]));
    }

    private static string? LastNonBlankLine(string text)
    {
        string? last = null;
        foreach (var range in new GraphemeString(text).Split(g => g.IsNewline))
        {
            string line = text[range];
            if (ScalarSets.TrimStart(line, ScalarSets.IsWhitespace).Length > 0) last = line;
        }
        return last;
    }

    /// <summary>
    /// Lower-cased with surrounding whitespace, dashes and closing punctuation removed and interior
    /// whitespace collapsed, so "Best,", "Thanks again!" and "kind  regards" with a colon compare as their words.
    /// </summary>
    private static string Normalized(string line) =>
        string.Join(' ', GraphemeString.Words(ScalarSets.Trim(line, IsEdge).ToLowerInvariant()));

    private static bool IsEdge(Rune scalar) =>
        ScalarSets.IsWhitespace(scalar) || ScalarSets.IsAnyOf(scalar, EdgePunctuation);
}
