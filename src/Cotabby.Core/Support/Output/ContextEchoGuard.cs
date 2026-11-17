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
    /// <summary>The shortest run of words counted as copying rather than borrowing a phrase.</summary>
    public const int MinCopiedRun = 6;

    /// <summary>The completion cut before the first copied part, or unchanged when nothing was copied.</summary>
    public static string Trim(string completion, string context, string precedingText)
    {
        if (completion.Length == 0 || string.IsNullOrWhiteSpace(context)) return completion;

        var screenLines = context.Split('\n')
            .Select(Words)
            .Where(words => words.Count > 0)
            .ToList();
        var screenWords = screenLines.SelectMany(line => line.Select(w => w.Text)).ToList();
        var ownWords = Words(precedingText).Select(w => w.Text).ToList();
        var screenLineKeys = screenLines.Select(LineKey).ToHashSet(StringComparer.Ordinal);

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
            if (wholeLine && !ContainsRun(ownWords, words.Select(w => w.Text).ToList(), 0, words.Count))
            {
                cut = Math.Min(cut, firstLine ? lineStart + words[0].Start : Math.Max(0, lineStart - 1));
                break;
            }
            // A long run of words lifted from the screen, inside any line.
            var texts = words.Select(w => w.Text).ToList();
            for (int i = 0; i + MinCopiedRun <= texts.Count; i++)
            {
                if (ContainsRun(screenWords, texts, i, MinCopiedRun) && !ContainsRun(ownWords, texts, i, MinCopiedRun))
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

    /// <summary>Lowercased runs of letters and digits, so OCR's lost punctuation still matches.</summary>
    private static List<Word> Words(string text)
    {
        var words = new List<Word>();
        var current = new StringBuilder();
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (char.IsLetterOrDigit(text[i]))
            {
                if (current.Length == 0) start = i;
                current.Append(char.ToLowerInvariant(text[i]));
            }
            else if (current.Length > 0)
            {
                words.Add(new Word(current.ToString(), start));
                current.Clear();
            }
        }
        if (current.Length > 0) words.Add(new Word(current.ToString(), start));
        return words;
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
