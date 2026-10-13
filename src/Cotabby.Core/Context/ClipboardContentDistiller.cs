using Cotabby.Core.Text;

namespace Cotabby.Core.Context;

/// <summary>
/// Keeps only the clipboard lines that share a meaningful word with the text before the caret.
/// </summary>
public static class ClipboardContentDistiller
{
    private const int CompactLineThreshold = 3;
    private const int HeadFallbackCharacters = 300;

    /// <summary>
    /// A clipboard of up to three lines, or a prefix with no significant words, passes through
    /// unchanged. A longer clipboard keeps its overlapping lines, or its first 300 characters
    /// when no line overlaps.
    /// </summary>
    public static string Distill(string clipboard, string prefixText)
    {
        var lines = clipboard.Split('\n');
        if (lines.Length <= CompactLineThreshold) return clipboard;

        var prefixTokens = PromptContextSanitizer.SignificantTokens(prefixText);
        if (prefixTokens.Count == 0) return clipboard;

        var relevant = lines.Where(line => PromptContextSanitizer.SignificantTokens(line).Overlaps(prefixTokens)).ToList();
        return relevant.Count == 0
            ? new GraphemeString(clipboard).Prefix(HeadFallbackCharacters)
            : string.Join('\n', relevant);
    }
}
