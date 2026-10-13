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
            // A correction replaces a word as a unit, so typing never advances it.
            case KeyKind.Character when session.Correction is null
                && session.VisibleText.Length > 0 && session.VisibleText[0] == ev.Character:
                return new(Outcome.Advance, session with { VisibleText = session.VisibleText[1..] });

            case KeyKind.Character or KeyKind.Tab or KeyKind.Backspace or KeyKind.Delete
                or KeyKind.Escape or KeyKind.Enter or KeyKind.Arrow or KeyKind.Shortcut:
                return new(Outcome.Cancel, null);

            default:
                return new(Outcome.Ignore, session);
        }
    }
}
