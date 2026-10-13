using Cotabby.Core.Input;

namespace Cotabby.Core.Macros;

/// <summary>
/// Pure state machine deciding when an inline <c>/macro</c> capture is active. It owns only
/// the capture lifecycle and the live query. Evaluation lives in <see cref="MacroEngine"/>.
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item><c>/</c> at a word boundary (field start or after whitespace) opens a capture.</item>
///   <item>Characters accepted by <see cref="MacroQueryGrammar"/> extend the query, including
///   a later <c>/</c>, which is division.</item>
///   <item>Tab commits and is consumed when there is a result. Without one it cancels and
///   passes through.</item>
///   <item>Escape cancels and is consumed.</item>
///   <item>Any other character, Enter, Delete, arrows, a Ctrl, Alt or Win chord, and Backspace
///   on an empty query cancel and pass through.</item>
/// </list>
/// </remarks>
public sealed class MacroTriggerStateMachine
{
    public enum Outcome
    {
        /// <summary>No effect on macro state.</summary>
        None,
        /// <summary>A capture opened with an empty query.</summary>
        Opened,
        /// <summary>The query grew or shrank. <see cref="Query"/> is current.</summary>
        QueryChanged,
        /// <summary>The caller should replace the typed run with the result.</summary>
        Commit,
        /// <summary>The capture ended and the literal text stays untouched.</summary>
        Cancel,
    }

    private const char Trigger = '/';

    // Last character typed while idle. Null means a boundary such as the start of a field.
    private char? _previousCharacter;

    /// <summary>Current query text (without the leading slash). Empty when inactive.</summary>
    public string Query { get; private set; } = "";

    /// <summary>True while a capture is active.</summary>
    public bool IsActive { get; private set; }

    /// <summary>True when the key passed to the most recent <see cref="Apply"/> must be suppressed.</summary>
    public bool ConsumesKey { get; private set; }

    /// <summary>Query captured when the most recent <see cref="Outcome.Commit"/> fired.</summary>
    public string LastCommittedQuery { get; private set; } = "";

    /// <summary>Length of the typed <c>/query</c> run to erase for the most recent commit.</summary>
    public int QueryAtCommitLength => LastCommittedQuery.Length + 1;

    /// <param name="ev">The key event to apply.</param>
    /// <param name="hasInsertableResult">
    /// Whether the current query evaluates to a result. Only matters for Tab, where it decides
    /// between consuming the key to commit and letting it reach the focused app.
    /// </param>
    public Outcome Apply(KeyboardEvent ev, bool hasInsertableResult)
    {
        ConsumesKey = false;
        if (!ev.IsKeyDown) return Outcome.None;
        if (ev.HasNonShiftModifier) return End();

        switch (ev.Kind)
        {
            case KeyKind.Character:
                return ApplyCharacter(ev.Character);

            case KeyKind.Backspace:
                if (!IsActive || Query.Length == 0) return End();
                Query = Query[..^1];
                return Outcome.QueryChanged;

            case KeyKind.Tab:
                if (!IsActive || !hasInsertableResult) return End();
                LastCommittedQuery = Query;
                End();
                ConsumesKey = true;
                return Outcome.Commit;

            case KeyKind.Escape:
                ConsumesKey = IsActive;
                return End();

            case KeyKind.Enter:
            case KeyKind.Delete:
            case KeyKind.Arrow:
                return End();

            default:
                // Bare modifier presses arrive as Other and must not end a capture.
                return Outcome.None;
        }
    }

    public void Reset()
    {
        End();
        LastCommittedQuery = "";
    }

    private Outcome ApplyCharacter(char c)
    {
        if (IsActive)
        {
            if (!MacroQueryGrammar.Extends(c)) return End(c);
            Query += c;
            return Outcome.QueryChanged;
        }

        if (c == Trigger && IsBoundary(_previousCharacter))
        {
            IsActive = true;
            return Outcome.Opened;
        }
        _previousCharacter = c;
        return Outcome.None;
    }

    private Outcome End(char? previousCharacter = null)
    {
        var outcome = IsActive ? Outcome.Cancel : Outcome.None;
        IsActive = false;
        Query = "";
        _previousCharacter = previousCharacter;
        return outcome;
    }

    // Keeps and/or, http:// and 1/2 from opening a macro mid-word.
    private static bool IsBoundary(char? previous) => previous is not { } c || char.IsWhiteSpace(c);
}

/// <summary>
/// Characters that extend a macro query. Deliberately independent of regional settings, so
/// decimals are <c>.</c>, argument lists use <c>,</c> and conversions use <c>-&gt;</c>.
/// </summary>
public static class MacroQueryGrammar
{
    public static bool Extends(char c) =>
        char.IsLetter(c) || char.IsNumber(c) || "+-*/^%(),.=>".Contains(c);
}
