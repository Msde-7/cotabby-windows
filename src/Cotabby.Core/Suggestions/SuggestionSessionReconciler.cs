using Cotabby.Core.Input;
using Cotabby.Core.Support;

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

    /// <summary>
    /// The next chunk of <paramref name="visibleText"/> a word-granularity
    /// accept should take: leading whitespace plus one word. Mirrors the macOS
    /// <c>nextAcceptanceChunk</c>. When
    /// <paramref name="acceptTrailingPunctuation"/> is false the chunk is cut
    /// after its last alphanumeric so trailing punctuation stays as its own
    /// next chunk (interior punctuation like "don't" is preserved).
    /// </summary>
    public static string NextAcceptanceChunk(string visibleText, bool acceptTrailingPunctuation = true)
    {
        if (visibleText.Length == 0) return string.Empty;

        int i = 0;
        while (i < visibleText.Length && char.IsWhiteSpace(visibleText[i])) i++;
        int wordStart = i;
        while (i < visibleText.Length && !char.IsWhiteSpace(visibleText[i])) i++;
        if (wordStart == i) return visibleText; // all whitespace — take it all

        var chunk = visibleText[..i];
        if (!acceptTrailingPunctuation)
        {
            int lastAlnum = -1;
            for (int j = chunk.Length - 1; j >= wordStart; j--)
            {
                if (char.IsLetterOrDigit(chunk[j])) { lastAlnum = j; break; }
            }
            if (lastAlnum >= 0 && lastAlnum < chunk.Length - 1)
            {
                chunk = chunk[..(lastAlnum + 1)];
            }
        }
        return chunk;
    }

    /// <summary>
    /// The next phrase-granularity chunk: whole words until a newline or a
    /// sentence terminator ends the phrase, or the tail is exhausted. Mirrors
    /// the macOS <c>nextAcceptancePhrase</c>.
    /// </summary>
    public static string NextAcceptancePhrase(string visibleText, bool acceptTrailingPunctuation = true)
    {
        int consumed = 0;
        while (consumed < visibleText.Length)
        {
            var chunk = NextAcceptanceChunk(visibleText[consumed..], acceptTrailingPunctuation);
            if (chunk.Length == 0) break;
            consumed += chunk.Length;

            int nl = chunk.IndexOf('\n');
            if (nl >= 0)
            {
                consumed -= chunk.Length - (nl + 1);
                break;
            }
            if (SentenceBoundaryClassifier.EndsSentence(visibleText[..consumed])) break;
        }
        return visibleText[..consumed];
    }

    /// <summary>
    /// Reconciles an accepted chunk against the live text before the caret:
    /// drops the chunk's leading whitespace when the host text already ends
    /// in horizontal whitespace, and injects a single separating space when
    /// two word characters would otherwise fuse. Mirrors the macOS
    /// <c>insertionChunk</c>.
    /// </summary>
    public static string InsertionChunk(string chunk, string livePrecedingText)
    {
        if (chunk.Length == 0) return chunk;
        if (livePrecedingText.Length > 0)
        {
            char last = livePrecedingText[^1];
            if (last is ' ' or '\t')
            {
                return chunk.TrimStart(' ', '\t');
            }
            if (char.IsLetterOrDigit(chunk[0]) && char.IsLetterOrDigit(last))
            {
                return " " + chunk;
            }
        }
        return chunk;
    }
}
