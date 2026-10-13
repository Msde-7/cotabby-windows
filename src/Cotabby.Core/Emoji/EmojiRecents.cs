namespace Cotabby.Core.Emoji;

/// <summary>
/// Builds the suggestions shown for a bare colon. Personal recents come first (most recent first),
/// then the curated popularity prior fills the panel.
/// </summary>
public static class EmojiRecents
{
    public static IReadOnlyList<EmojiMatch> Suggestions(
        EmojiUsageSnapshot usage,
        EmojiCatalog catalog,
        int limit = EmojiMatcher.DefaultLimit)
    {
        if (limit <= 0) return [];

        // The padding slice is generous so unresolvable or duplicate recents still leave enough
        // aliases to fill the limit.
        var orderedAliases = usage.RecentAliases
            .Concat(EmojiPopularity.StarterAliases(limit * 3))
            .Select(alias => alias.ToLowerInvariant())
            .Distinct();

        var result = new List<EmojiMatch>();
        foreach (var alias in orderedAliases)
        {
            if (catalog.EntryForAlias(alias) is not { } entry) continue;
            result.Add(new EmojiMatch(entry));
            if (result.Count >= limit) break;
        }
        return result;
    }
}
