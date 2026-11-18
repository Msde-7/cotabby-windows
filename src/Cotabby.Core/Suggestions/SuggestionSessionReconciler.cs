using Cotabby.Core.Input;

namespace Cotabby.Core.Suggestions;

/// <summary>
/// Pure rules for reconciling a visible <see cref="SuggestionSession"/> against a
/// key press. The coordinator owns the session and the side effects.
/// </summary>
public static class SuggestionSessionReconciler
{
    public enum Outcome
    {
        /// <summary>The key does not affect the suggestion.</summary>
        Ignore,

        /// <summary>The typed character matched the next character of the suggestion.</summary>
        Advance,

        /// <summary>The key invalidated the suggestion.</summary>
        Cancel,
    }

    public readonly record struct Result(Outcome Outcome, SuggestionSession? Next);

    public static Result Apply(SuggestionSession session, KeyboardEvent ev)
    {
        if (!ev.IsKeyDown) return new(Outcome.Ignore, session);

        switch (ev.Kind)
        {
            // A correction replaces a word as a unit, so typing never advances it. Typing may run
            // on into predicted words that are not shown yet.
            case KeyKind.Character when session.Correction is null
                && session.PredictedText.Length > 0 && SameKey(session.PredictedText[0], ev.Character):
                return new(Outcome.Advance, session.Advancing(1));

            case KeyKind.Character or KeyKind.Tab or KeyKind.Backspace or KeyKind.Delete
                or KeyKind.Escape or KeyKind.Enter or KeyKind.Arrow or KeyKind.Shortcut:
                return new(Outcome.Cancel, null);

            default:
                return new(Outcome.Ignore, session);
        }
    }

    /// <summary>
    /// Chromium and some editors publish a space typed at the end of a line as U+00A0 and turn it
    /// back into a plain space once the next character arrives. Both are the same key to the
    /// user, so every comparison treats them alike. Each is one UTF-16 unit, so offsets hold.
    /// </summary>
    public static string SpaceNormalized(string text) => text.Replace(NoBreakSpace, ' ');

    private const char NoBreakSpace = '\u00A0';

    /// <summary>
    /// <see cref="SpaceNormalized"/> with every line ending as LF. Cotabby inserts LF, but Notepad
    /// and RichEdit read it back as CR LF or a lone CR. Offsets do not hold, so the result is only
    /// for comparing texts, and lengths are only compared between two normalized texts.
    /// </summary>
    public static string ComparisonNormalized(string text)
    {
        text = SpaceNormalized(text);
        return text.Contains('\r') ? text.Replace("\r\n", "\n").Replace('\r', '\n') : text;
    }

    /// <summary>
    /// True when <paramref name="live"/> is the expected text, or the expected text less a few
    /// trailing characters the host has not published yet. A long text is compared through its
    /// last <paramref name="comparedTail"/> characters, since the field is read through a moving window.
    /// </summary>
    public static bool FollowsExpected(string expected, string live, int allowedLag, int comparedTail = 256)
    {
        expected = ComparisonNormalized(expected);
        live = ComparisonNormalized(live);
        // A short text is the whole of what precedes the caret, so it has to match from the start.
        if (live.Length < comparedTail)
        {
            return expected.Length - live.Length <= allowedLag && expected.StartsWith(live, StringComparison.Ordinal);
        }
        int at = expected.LastIndexOf(live[^comparedTail..], StringComparison.Ordinal);
        return at >= 0 && expected.Length - (at + comparedTail) <= allowedLag;
    }

    private static bool SameKey(char shown, char typed) =>
        shown == typed || (shown is ' ' or NoBreakSpace && typed is ' ' or NoBreakSpace);
}
