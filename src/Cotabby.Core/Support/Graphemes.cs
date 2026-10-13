using System.Globalization;
using System.Text;

namespace Cotabby.Core.Support;

/// <summary>
/// One user-perceived character (an extended grapheme cluster), the unit the upstream policies count
/// and classify. Classification reads the first scalar, so a letter carrying a combining mark is
/// still a letter.
/// </summary>
internal readonly struct Grapheme
{
    private readonly string _text;
    private readonly int _start;
    private readonly int _length;

    public Grapheme(string text, int start, int length)
    {
        _text = text;
        _start = start;
        _length = length;
    }

    public ReadOnlySpan<char> Span => _text.AsSpan(_start, _length);

    public Rune Scalar
    {
        get
        {
            Rune.DecodeFromUtf16(Span, out var rune, out _);
            return rune;
        }
    }

    /// <summary>Alphabetic. Letter numbers such as Roman numerals count, as they do upstream.</summary>
    public bool IsLetter =>
        Rune.IsLetter(Scalar) || Rune.GetUnicodeCategory(Scalar) == UnicodeCategory.LetterNumber;

    public bool IsNumber => Rune.IsNumber(Scalar);

    public bool IsWhitespace
    {
        get
        {
            var scalar = Scalar;
            // .NET also classes the U+001C to U+001F separators as white space. Unicode does not.
            return Rune.IsWhiteSpace(scalar) && scalar.Value is < 0x1C or > 0x1F;
        }
    }

    public bool IsNewline => ScalarSets.IsNewline(Scalar);
    public bool IsPunctuation => Rune.IsPunctuation(Scalar);
    public bool IsSymbol => Rune.IsSymbol(Scalar);
    public bool IsUppercase => Rune.IsUpper(Scalar);
    public bool IsLowercase => Rune.IsLower(Scalar);

    /// <summary>True when this is exactly the single UTF-16 unit <paramref name="character"/>.</summary>
    public bool Is(char character) => _length == 1 && _text[_start] == character;

    /// <summary>True when this is exactly one of the single-unit characters in <paramref name="set"/>.</summary>
    public bool IsAnyOf(string set) => _length == 1 && set.Contains(_text[_start]);

    public bool Equals(Grapheme other) => Span.SequenceEqual(other.Span);

    public override string ToString() => _text.Substring(_start, _length);
}

/// <summary>
/// A string indexed by grapheme, so counts and slices agree with what the user sees. A surrogate
/// pair, a base letter with combining marks and a CRLF pair are each one element.
/// </summary>
internal readonly struct GraphemeString
{
    private readonly int[]? _starts;

    public GraphemeString(string text)
    {
        Text = text;
        _starts = IsOneUnitPerGrapheme(text) ? null : StringInfo.ParseCombiningCharacters(text);
    }

    public string Text { get; }

    public int Count => _starts?.Length ?? Text.Length;

    public Grapheme this[int index] => new(Text, Offset(index), Offset(index + 1) - Offset(index));

    public Grapheme? First => Count > 0 ? this[0] : null;

    public Grapheme? Last => Count > 0 ? this[Count - 1] : null;

    /// <summary>UTF-16 offset of the grapheme at <paramref name="index"/>, or the text length at the end.</summary>
    public int Offset(int index) => index >= Count ? Text.Length : _starts?[index] ?? index;

    public string Prefix(int count) => Text[..Offset(Math.Clamp(count, 0, Count))];

    public string Suffix(int count) => Text[Offset(Count - Math.Clamp(count, 0, Count))..];

    /// <summary>True when the text begins with <paramref name="prefix"/> and the prefix ends on a grapheme boundary.</summary>
    public bool StartsWith(string prefix) =>
        Text.StartsWith(prefix, StringComparison.Ordinal)
        && (_starts is null || prefix.Length == Text.Length || Array.BinarySearch(_starts, prefix.Length) >= 0);

    /// <summary>Number of leading graphemes, starting at <paramref name="from"/>, that satisfy the predicate.</summary>
    public int PrefixLength(Func<Grapheme, bool> predicate, int from = 0)
    {
        int index = from;
        while (index < Count && predicate(this[index])) index++;
        return index - from;
    }

    /// <summary>Number of trailing graphemes that satisfy the predicate.</summary>
    public int SuffixLength(Func<Grapheme, bool> predicate)
    {
        int index = Count;
        while (index > 0 && predicate(this[index - 1])) index--;
        return Count - index;
    }

    /// <summary>UTF-16 ranges of the maximal runs between separator graphemes, in order.</summary>
    public List<Range> Split(Func<Grapheme, bool> isSeparator)
    {
        var ranges = new List<Range>();
        int start = -1;
        for (int index = 0; index < Count; index++)
        {
            if (isSeparator(this[index]))
            {
                if (start >= 0) ranges.Add(Offset(start)..Offset(index));
                start = -1;
            }
            else if (start < 0)
            {
                start = index;
            }
        }
        if (start >= 0) ranges.Add(Offset(start)..Text.Length);
        return ranges;
    }

    /// <summary>The whitespace-separated words of <paramref name="text"/>, in order.</summary>
    public static List<string> Words(string text)
    {
        var words = new List<string>();
        foreach (var range in new GraphemeString(text).Split(g => g.IsWhitespace)) words.Add(text[range]);
        return words;
    }

    public static int CountOf(string text) => new GraphemeString(text).Count;

    public static Grapheme? FirstOf(string text)
    {
        if (text.Length == 0) return null;
        return new Grapheme(text, 0, StringInfo.GetNextTextElementLength(text));
    }

    public static Grapheme? LastOf(string text)
    {
        if (text.Length == 0) return null;
        // An ordinary final unit cannot join the cluster before it. A line feed can (CRLF).
        char last = text[^1];
        if (last < CombiningStart && last != '\n') return new Grapheme(text, text.Length - 1, 1);
        return new GraphemeString(text).Last;
    }

    private const char CombiningStart = '̀';

    /// <summary>Below U+0300 only a carriage return can begin a multi-unit cluster.</summary>
    private static bool IsOneUnitPerGrapheme(string text)
    {
        foreach (char unit in text)
        {
            if (unit >= CombiningStart || unit == '\r') return false;
        }
        return true;
    }
}

/// <summary>Scalar classes matching the Foundation character sets the upstream rules are written against.</summary>
internal static class ScalarSets
{
    /// <summary>Space separators and tab.</summary>
    public static bool IsWhitespace(Rune scalar) =>
        scalar.Value == '\t' || Rune.GetUnicodeCategory(scalar) == UnicodeCategory.SpaceSeparator;

    public static bool IsNewline(Rune scalar) => IsNewline(scalar.Value);

    public static bool IsNewline(int scalar) => scalar is (>= 0x0A and <= 0x0D) or 0x85 or 0x2028 or 0x2029;

    public static bool IsWhitespaceOrNewline(Rune scalar) => IsWhitespace(scalar) || IsNewline(scalar);

    /// <summary>Control and format characters.</summary>
    public static bool IsControl(Rune scalar) =>
        Rune.GetUnicodeCategory(scalar) is UnicodeCategory.Control or UnicodeCategory.Format;

    /// <summary>Letters, marks and numbers.</summary>
    public static bool IsAlphanumeric(Rune scalar) =>
        Rune.GetUnicodeCategory(scalar) is
            UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter
            or UnicodeCategory.TitlecaseLetter or UnicodeCategory.ModifierLetter
            or UnicodeCategory.OtherLetter or UnicodeCategory.NonSpacingMark
            or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark
            or UnicodeCategory.DecimalDigitNumber or UnicodeCategory.LetterNumber
            or UnicodeCategory.OtherNumber;

    public static string Trim(string text, Func<Rune, bool> isTrimmed) =>
        TrimEnd(TrimStart(text, isTrimmed), isTrimmed);

    public static string TrimStart(string text, Func<Rune, bool> isTrimmed)
    {
        int start = 0;
        while (start < text.Length)
        {
            Rune.DecodeFromUtf16(text.AsSpan(start), out var scalar, out int consumed);
            if (!isTrimmed(scalar)) break;
            start += consumed;
        }
        return text[start..];
    }

    public static string TrimEnd(string text, Func<Rune, bool> isTrimmed)
    {
        int end = text.Length;
        while (end > 0)
        {
            Rune.DecodeLastFromUtf16(text.AsSpan(0, end), out var scalar, out int consumed);
            if (!isTrimmed(scalar)) break;
            end -= consumed;
        }
        return text[..end];
    }

    public static string TrimWhitespaceAndNewlines(string text) => Trim(text, IsWhitespaceOrNewline);

    public static bool IsBlank(string text)
    {
        foreach (var scalar in text.EnumerateRunes())
        {
            if (!IsWhitespaceOrNewline(scalar)) return false;
        }
        return true;
    }
}
