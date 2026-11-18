namespace Cotabby.Core.Support;

/// <summary>
/// Where a window over a growing text starts, chosen so that it stays put while the writer types at
/// the far end. The model keeps the prompt it evaluated last and only evaluates what changed after
/// the first differing character. A window cut exactly at its character or word budget starts one
/// character later on every keystroke, so every keystroke evaluated the whole window again.
/// </summary>
/// <remarks>
/// The start is snapped forward to an anchor, a word, sentence or line start picked by a hash of the
/// text right after it. An anchor depends only on that text, not on where the window or the
/// document begins, so the cut holds while the budget's own start slides over the anchor's
/// predecessors, including when the caller's text is itself the sliding tail of a longer document.
/// The window then keeps a few hundred characters fewer of its oldest text, and moves on to the
/// next anchor once its budget start passes this one.
/// </remarks>
public static class StableTextCut
{
    /// <summary>The farthest an anchored start moves past the budget's own start.</summary>
    public const int MaxAdvance = 2048;

    /// <summary>Characters after a candidate that pick whether it is an anchor.</summary>
    private const int HashSpan = 16;

    /// <summary>
    /// One word start in this many is an anchor, so a long prose window starts again every few
    /// hundred characters typed. Sentence and line starts are anchors four times as often.
    /// </summary>
    private const uint WordModulus = 64;
    private const uint SentenceModulus = 16;

    /// <summary>Denser anchors, used only when no regular anchor lies within reach.</summary>
    private const uint FallbackModulus = 4;

    private enum BreakKind { None, Word, Sentence }

    /// <summary>
    /// The first anchor in <paramref name="text"/> at or after <paramref name="start"/> and at most
    /// <paramref name="maxAdvance"/> characters past it, or <paramref name="start"/> itself when there
    /// is none. An anchor always starts a word with a letter or digit, so it never splits a word,
    /// a surrogate pair or a grapheme.
    /// </summary>
    public static int WindowStart(string text, int start, int maxAdvance)
    {
        int last = Math.Min(text.Length - HashSpan, start + Math.Min(maxAdvance, MaxAdvance));
        int fallback = -1;
        for (int j = Math.Max(start, 1); j <= last; j++)
        {
            var kind = Kind(text, j);
            if (kind == BreakKind.None) continue;
            uint hash = Hash(text, j);
            if (hash % (kind == BreakKind.Word ? WordModulus : SentenceModulus) == 0) return j;
            if (fallback < 0 && hash % FallbackModulus == 0) fallback = j;
        }
        return fallback >= 0 ? fallback : start;
    }

    /// <summary>
    /// Whether a word starts at <paramref name="index"/>: a letter or digit after whitespace or after
    /// punctuation that ends a word. Neither character may be half of a surrogate pair, so the cut
    /// is a grapheme boundary.
    /// </summary>
    private static BreakKind Kind(string text, int index)
    {
        char current = text[index];
        char previous = text[index - 1];
        if (char.IsSurrogate(current) || char.IsSurrogate(previous) || !char.IsLetterOrDigit(current))
        {
            return BreakKind.None;
        }
        if (previous is '\n' or '\r' or '。' or '！' or '？') return BreakKind.Sentence;
        if (char.IsWhiteSpace(previous))
        {
            int before = index - 1;
            while (before > 0 && index - before < 8 && text[before] is ' ' or '\t') before--;
            return text[before] is '.' or '!' or '?' ? BreakKind.Sentence : BreakKind.Word;
        }
        // Apostrophes, hyphens and dots join the parts of one word ("don't", "e-mail", "3.5").
        return char.IsPunctuation(previous) && previous is not ('\'' or '’' or '-' or '_' or '.' or '‐')
            ? BreakKind.Word
            : BreakKind.None;
    }

    /// <summary>FNV-1a over the character before <paramref name="index"/> and the span after it.</summary>
    private static uint Hash(string text, int index)
    {
        uint hash = 2166136261;
        for (int k = index - 1; k < index + HashSpan; k++) hash = (hash ^ text[k]) * 16777619;
        return hash;
    }
}
