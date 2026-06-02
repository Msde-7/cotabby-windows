using Cotabby.Core.Input;

namespace Cotabby.Core.Suggestions;

/// <summary>
/// Pure rules for reconciling a live <see cref="SuggestionSession"/> against the
/// user's typing. Mirrors the macOS port's <c>SuggestionSessionReconciler</c>.
///
/// The reconciler does not own the session or fire side effects — it only
/// reports the *outcome* of applying an event to a session. The coordinator
/// uses the outcome to decide whether to update overlay text, hide the overlay,
/// or fire a fresh generation.
/// </summary>
public static class SuggestionSessionReconciler
{
    public enum Outcome
    {
        /// <summary>No active session, or the event is irrelevant.</summary>
        Ignore,

        /// <summary>The typed character matched the next character of the suggestion;
        /// shrink the visible text and keep going.</summary>
        AdvanceVisible,

        /// <summary>The typed character did not match; cancel the session.</summary>
        Cancel,

        /// <summary>Tab was pressed and a session is visible; coordinator should accept.</summary>
        Accept,
    }

    public readonly record struct Result(Outcome Outcome, SuggestionSession? Next);

    public static Result Apply(SuggestionSession? session, KeyboardEvent ev)
    {
        if (session is null) return new(Outcome.Ignore, null);
        if (!ev.IsKeyDown) return new(Outcome.Ignore, session);

        switch (ev.Kind)
        {
            case KeyKind.Tab:
                if (session.VisibleText.Length > 0) return new(Outcome.Accept, session);
                return new(Outcome.Ignore, session);

            case KeyKind.Escape or KeyKind.Enter or KeyKind.Arrow or KeyKind.Delete:
                return new(Outcome.Cancel, null);

            case KeyKind.Backspace:
                // Backspace always invalidates — the prefix the suggestion was
                // generated against no longer matches what's in the field.
                return new(Outcome.Cancel, null);

            case KeyKind.Character:
                if (session.VisibleText.Length == 0) return new(Outcome.Ignore, session);
                if (session.VisibleText[0] == ev.Character)
                {
                    var next = session with
                    {
                        VisibleText = session.VisibleText[1..],
                        ConsumedChars = session.ConsumedChars + 1,
                    };
                    return new(Outcome.AdvanceVisible, next);
                }
                return new(Outcome.Cancel, null);

            default:
                return new(Outcome.Ignore, session);
        }
    }
}
