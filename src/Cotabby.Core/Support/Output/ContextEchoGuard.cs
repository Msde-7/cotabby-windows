using System.Text;

namespace Cotabby.Core.Support.Output;

/// <summary>
/// Stops a suggestion from copying context the model was shown but the user never wrote: the text
/// on screen, and the prompt's own description of the writer's language and app. With nothing better
/// to say, a base model may continue that text, such as a dialog, a status bar or the language note.
/// The user's notes, clipboard and earlier writing are left out on purpose, since reusing them is
/// what they are there for.
/// A name or a short phrase taken from the screen is what screen context is for, so only three
/// shapes are cut: a later line of the suggestion that reproduces a whole line on screen, any line
/// that reproduces a screen line carrying a number (a status bar or counter), and a run of
/// <see cref="MinCopiedRun"/> words taken from the screen that the user's own text does not hold.
/// </summary>
public static class ContextEchoGuard
{
    /// <summary>
    /// The shortest run of words counted as copying rather than borrowing a phrase. A Chinese or
    /// Japanese character counts as half a word.
    /// </summary>
    public const int MinCopiedRun = 6;

    /// <summary>
    /// The context and the user's own text split into words once, for checking every partial of
    /// one streamed completion against them.
    /// </summary>
    public sealed class Prepared
    {
        private readonly Lazy<List<string>> _ownWords;

        internal Prepared(string context, string precedingText)
        {
            IsEmpty = string.IsNullOrWhiteSpace(context);
            var screenLines = IsEmpty
                ? []
                : context.Split('\n').Select(Words).Where(words => words.Count > 0).ToList();
            ScreenWords = screenLines.SelectMany(line => line.Select(w => w.Text)).ToList();
            ScreenLineKeys = screenLines.Select(LineKey).ToHashSet(StringComparer.Ordinal);
            // Needed only once a run matches the screen, which most completions never do.
            _ownWords = new(() => Words(precedingText).Select(w => w.Text).ToList());
        }

        internal bool IsEmpty { get; }

        internal List<string> ScreenWords { get; }

        internal HashSet<string> ScreenLineKeys { get; }

        internal List<string> OwnWords => _ownWords.Value;
    }

    /// <summary>Splits <paramref name="context"/> and <paramref name="precedingText"/> for <see cref="Trim(string, Prepared)"/>.</summary>
    public static Prepared Prepare(string context, string precedingText) => new(context, precedingText);

    /// <summary>The completion cut before the first copied part, or unchanged when nothing was copied.</summary>
    public static string Trim(string completion, string context, string precedingText) =>
        Trim(completion, Prepare(context, precedingText));

    /// <summary><see cref="Trim(string, string, string)"/> against a context split once per request.</summary>
    public static string Trim(string completion, Prepared prepared)
    {
        if (completion.Length == 0 || prepared.IsEmpty) return completion;

        var screenWords = prepared.ScreenWords;
        var screenLineKeys = prepared.ScreenLineKeys;

        int cut = completion.Length;
        int lineStart = 0;
        bool firstLine = true;
        foreach (string line in completion.Split('\n'))
        {
            var words = Words(line);
            // A line that is a line on screen word for word. On the first line only a line carrying a
            // number counts, which is how status bars, counters and timestamps read, since a heading
            // or name copied whole is a fair use of the screen.
            bool wholeLine = words.Count > 0 && screenLineKeys.Contains(LineKey(words))
                && (!firstLine || words.Any(w => w.Text.All(char.IsDigit)));
            if (wholeLine && !ContainsRun(prepared.OwnWords, words.Select(w => w.Text).ToList(), 0, words.Count))
            {
                cut = Math.Min(cut, firstLine ? lineStart + words[0].Start : Math.Max(0, lineStart - 1));
                break;
            }
            // A long run of words lifted from the screen, inside any line.
            var texts = words.Select(w => w.Text).ToList();
            for (int i = 0; i < texts.Count; i++)
            {
                int length = CopiedRunLength(texts, i);
                if (length == 0) break;
                if (ContainsRun(screenWords, texts, i, length) && !ContainsRun(prepared.OwnWords, texts, i, length))
                {
                    cut = Math.Min(cut, lineStart + words[i].Start);
                    break;
                }
            }
            if (cut < completion.Length) break;
            lineStart += line.Length + 1;
            firstLine = false;
        }
        return cut >= completion.Length ? completion : completion[..cut].TrimEnd();
    }

    private readonly record struct Word(string Text, int Start);

    /// <summary>
    /// A line's words with every number standing as "#", so a counter or caret position on screen
    /// matches the same line with other numbers in it ("Ln 1 Col 39" and "Ln 1 Col 1").
    /// </summary>
    private static string LineKey(List<Word> words) =>
        string.Join(' ', words.Select(w => w.Text.All(char.IsDigit) ? "#" : w.Text));

    /// <summary>
    /// Lowercased runs of letters and digits, so OCR's lost punctuation still matches. Chinese and
    /// Japanese put no spaces between words, so each ideograph or kana stands as a word of its own.
    /// </summary>
    private static List<Word> Words(string text)
    {
        var words = new List<Word>();
        var current = new StringBuilder();
        int start = 0;
        int i = 0;
        Span<char> lowered = stackalloc char[2];
        foreach (var rune in text.EnumerateRunes())
        {
            if (IsUnspacedCharacter(rune))
            {
                Flush();
                words.Add(new Word(rune.ToString(), i));
            }
            else if (Rune.IsLetterOrDigit(rune))
            {
                if (current.Length == 0) start = i;
                // Appended as UTF-16 units, so a long screen text costs no string per letter.
                current.Append(lowered[..Rune.ToLowerInvariant(rune).EncodeToUtf16(lowered)]);
            }
            else
            {
                Flush();
            }
            i += rune.Utf16SequenceLength;
        }
        Flush();
        return words;

        void Flush()
        {
            if (current.Length == 0) return;
            words.Add(new Word(current.ToString(), start));
            current.Clear();
        }
    }

    /// <summary>
    /// Kana and CJK ideographs, which are written without spaces between words. Hangul is left out
    /// because Korean puts spaces between words.
    /// </summary>
    private static bool IsUnspacedCharacter(Rune rune) => Rune.IsLetter(rune) && rune.Value
        is (>= 0x3040 and <= 0x30FF) or (>= 0x3400 and <= 0x4DBF) or (>= 0x4E00 and <= 0x9FFF)
        or (>= 0xF900 and <= 0xFAFF) or (>= 0xFF66 and <= 0xFF9F) or (>= 0x20000 and <= 0x3134F);

    /// <summary>
    /// How many words from <paramref name="from"/> make a copied run, or 0 when the rest of the line
    /// is too short. A Chinese or Japanese word is usually two characters, so one such character
    /// counts as half a word and a copied run spans twice as many of them.
    /// </summary>
    private static int CopiedRunLength(List<string> words, int from)
    {
        double weight = 0;
        for (int i = from; i < words.Count; i++)
        {
            weight += IsUnspacedCharacter(Rune.GetRuneAt(words[i], 0)) ? 0.5 : 1;
            if (weight >= MinCopiedRun) return i - from + 1;
        }
        return 0;
    }

    private static bool ContainsRun(List<string> haystack, List<string> needle, int from, int length)
    {
        for (int i = 0; i + length <= haystack.Count; i++)
        {
            int j = 0;
            while (j < length && haystack[i + j] == needle[from + j]) j++;
            if (j == length) return true;
        }
        return false;
    }
}
