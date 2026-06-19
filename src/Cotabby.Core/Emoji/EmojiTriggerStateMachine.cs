using Cotabby.Core.Input;

namespace Cotabby.Core.Emoji;

/// <summary>
/// Pure state machine deciding when an emoji trigger is active. Mirrors the
/// macOS port's <c>EmojiTriggerStateMachine</c>. Owns no I/O — the controller
/// feeds it keyboard events and reads back the query state.
/// </summary>
/// <remarks>
/// Rules:
/// <list type="bullet">
///   <item><c>:</c> at a word boundary starts a query.</item>
///   <item>Alphanumerics / <c>+</c> / <c>-</c> / <c>_</c> extend the query.</item>
///   <item>Space / Enter / Escape / Tab / Backspace cancel (Backspace if the
///   query is empty cancels; otherwise it shrinks).</item>
///   <item>A second <c>:</c> commits the current query (user typed <c>:smile:</c>).</item>
/// </list>
/// </remarks>
public sealed class EmojiTriggerStateMachine
{
    public enum Outcome
    {
        /// <summary>No effect on emoji state.</summary>
        None,
        /// <summary>The trigger started or extended; <see cref="Query"/> is current.</summary>
        QueryChanged,
        /// <summary>User pressed the trailing colon — caller should insert the top match.</summary>
        Commit,
        /// <summary>User abandoned the trigger.</summary>
        Cancel,
    }

    /// <summary>Current query text (without leading colon). Empty when inactive.</summary>
    public string Query { get; private set; } = "";

    /// <summary>True while a trigger is active.</summary>
    public bool IsActive { get; private set; }

    /// <summary>
    /// Query string captured at the moment the most recent <see cref="Outcome.Commit"/>
    /// fired. Lets the controller compute how many characters to backspace
    /// after the state machine has already reset the live <see cref="Query"/>.
    /// </summary>
    public string LastCommittedQuery { get; private set; } = "";

    /// <summary>Convenience: <c>":query:".Length</c> for the most recent commit.</summary>
    public int QueryAtCommitLength => LastCommittedQuery.Length + 2;

    public Outcome Apply(KeyboardEvent ev)
    {
        if (!ev.IsKeyDown || ev.HasNonShiftModifier)
        {
            return Outcome.None;
        }

        switch (ev.Kind)
        {
            case KeyKind.Character:
                char c = ev.Character;
                if (c == ':')
                {
                    if (!IsActive)
                    {
                        IsActive = true;
                        Query = "";
                        return Outcome.QueryChanged;
                    }
                    // Trailing colon → commit.
                    var hadAny = Query.Length > 0;
                    if (hadAny) LastCommittedQuery = Query;
                    var outcome = hadAny ? Outcome.Commit : Outcome.Cancel;
                    Reset(preserveLastCommit: true);
                    return outcome;
                }
                if (!IsActive) return Outcome.None;

                if (IsValidQueryChar(c))
                {
                    Query += c;
                    return Outcome.QueryChanged;
                }

                // Whitespace / punctuation other than the allowed set cancels.
                Reset();
                return Outcome.Cancel;

            case KeyKind.Backspace:
                if (!IsActive) return Outcome.None;
                if (Query.Length == 0)
                {
                    Reset();
                    return Outcome.Cancel;
                }
                Query = Query[..^1];
                return Outcome.QueryChanged;

            case KeyKind.Escape:
            case KeyKind.Enter:
            case KeyKind.Tab:
            case KeyKind.Arrow:
            case KeyKind.Delete:
                if (!IsActive) return Outcome.None;
                Reset();
                return Outcome.Cancel;

            default:
                return Outcome.None;
        }
    }

    public void Reset() => Reset(preserveLastCommit: false);

    private void Reset(bool preserveLastCommit)
    {
        Query = "";
        IsActive = false;
        if (!preserveLastCommit) LastCommittedQuery = "";
    }

    private static bool IsValidQueryChar(char c) =>
        char.IsLetterOrDigit(c) || c == '+' || c == '-' || c == '_';
}
