using System.Collections.Generic;
using System.Linq;

namespace Cotabby.Core.Emoji;

/// <summary>
/// Pure substring matcher over <see cref="EmojiCatalog"/>. Mirrors the macOS
/// port's <c>EmojiMatcher</c>: prefix matches score higher than substring
/// matches, ties broken by name length so shorter names come first
/// (<c>:smile</c> over <c>:smile_eyes</c> for a query of "smile").
/// </summary>
public static class EmojiMatcher
{
    public static List<EmojiEntry> Search(string query, int max = 8)
    {
        if (string.IsNullOrWhiteSpace(query)) return new();
        var q = query.Trim().ToLowerInvariant();

        var scored = new List<(EmojiEntry Entry, int Score)>();
        foreach (var e in EmojiCatalog.All)
        {
            int s = Score(e, q);
            if (s > 0) scored.Add((e, s));
        }
        return scored
            .OrderByDescending(t => t.Score)
            .ThenBy(t => t.Entry.Name.Length)
            .ThenBy(t => t.Entry.Name, System.StringComparer.OrdinalIgnoreCase)
            .Take(max)
            .Select(t => t.Entry)
            .ToList();
    }

    private static int Score(EmojiEntry e, string q)
    {
        // Exact name match → highest. Prefix → high. Substring → medium.
        // Alias prefix / substring → lower (a step below the equivalent name match).
        int best = 0;
        string name = e.Name.ToLowerInvariant();
        if (name == q) best = Math.Max(best, 100);
        else if (name.StartsWith(q)) best = Math.Max(best, 70);
        else if (name.Contains(q)) best = Math.Max(best, 40);

        foreach (var alias in e.Aliases)
        {
            string a = alias.ToLowerInvariant();
            if (a == q) best = Math.Max(best, 90);
            else if (a.StartsWith(q)) best = Math.Max(best, 60);
            else if (a.Contains(q)) best = Math.Max(best, 30);
        }
        return best;
    }
}
