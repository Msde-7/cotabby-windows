namespace Cotabby.Core.Focus;

/// <summary>
/// The arithmetic of reading a field's text and caret line, kept apart from the accessibility
/// calls so it can be tested.
/// </summary>
public static class FieldReads
{
    /// <summary>
    /// The smallest rect holding all of <paramref name="rects"/>, or empty when they cover no
    /// width. Each rect counts as the host reported it, one with no width or height too.
    /// </summary>
    public static ScreenRect Union(ReadOnlySpan<ScreenRect> rects)
    {
        double left = double.MaxValue, top = double.MaxValue, right = double.MinValue, bottom = double.MinValue;
        foreach (var r in rects)
        {
            left = Math.Min(left, r.X);
            top = Math.Min(top, r.Y);
            right = Math.Max(right, r.Right);
            bottom = Math.Max(bottom, r.Bottom);
        }
        return right > left ? new ScreenRect(left, top, right - left, bottom - top) : ScreenRect.Empty;
    }

    /// <summary>True when the two rects share some area. Rects that only touch do not.</summary>
    public static bool Overlaps(ScreenRect a, ScreenRect b) =>
        a.X < b.Right && b.X < a.Right && a.Y < b.Bottom && b.Y < a.Bottom;

    /// <summary>
    /// True when text whose painted bounds are <paramref name="text"/> sits on the caret's line:
    /// its top is within half a line of the caret's. Text that wrapped onto lines above reaches
    /// higher. With no caret rect, any painted text counts.
    /// </summary>
    public static bool IsOnCaretLine(ScreenRect text, ScreenRect caret) =>
        !text.IsEmpty && (caret.IsEmpty || Math.Abs(text.Y - caret.Y) < Math.Max(caret.Height, 1) * 0.5);

    /// <summary>
    /// <paramref name="text"/> without a high surrogate at its end, which a read cut short at a
    /// character count can leave without its pair.
    /// </summary>
    public static string WithoutBrokenEnd(string text) =>
        text.Length > 0 && char.IsHighSurrogate(text[^1]) ? text[..^1] : text;

    /// <summary>
    /// <paramref name="text"/> without a low surrogate at its start, which keeping only the end of
    /// a longer text can leave without its pair.
    /// </summary>
    public static string WithoutBrokenStart(string text) =>
        text.Length > 0 && char.IsLowSurrogate(text[0]) ? text[1..] : text;
}
