using System.Text;

namespace Cotabby.Core.Support;

/// <summary>
/// Detects a completion that would duplicate the text already sitting after
/// the caret. Direct port of the macOS <c>TrailingDuplicationFilter</c>:
/// both strings are folded (lowercased, alphanumerics only) before
/// comparison so whitespace/punctuation differences don't hide a dup.
/// </summary>
public static class TrailingDuplicationFilter
{
    private const int MinimumFoldedOverlap = 3;

    public static bool DuplicatesTrailingText(string completion, string trailingText)
    {
        var foldedCompletion = Fold(completion);
        var foldedTrailing = Fold(trailingText);
        if (foldedCompletion.Length == 0 || foldedTrailing.Length == 0) return false;

        if (foldedCompletion.Length >= MinimumFoldedOverlap
            && foldedTrailing.StartsWith(foldedCompletion, StringComparison.Ordinal))
        {
            return true;
        }
        if (foldedTrailing.Length >= MinimumFoldedOverlap
            && foldedCompletion.StartsWith(foldedTrailing, StringComparison.Ordinal))
        {
            return true;
        }

        int common = CommonPrefixLength(foldedCompletion, foldedTrailing);
        return common >= Math.Max(MinimumFoldedOverlap, foldedCompletion.Length / 2);
    }

    private static string Fold(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    private static int CommonPrefixLength(string a, string b)
    {
        int n = Math.Min(a.Length, b.Length);
        int i = 0;
        while (i < n && a[i] == b[i]) i++;
        return i;
    }
}
