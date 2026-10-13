namespace Cotabby.Core.Emoji;

/// <summary>
/// The usage data the ranker reads to personalize results. Keyed by an emoji's primary alias, so
/// the signal is stable across skin tone and gender variants.
/// </summary>
public sealed class EmojiUsageSnapshot(
    IReadOnlyList<string> recentAliases,
    IReadOnlyDictionary<string, int> frequency)
{
    /// <summary>Commits before frequency alone marks an alias a favorite.</summary>
    public const int FrequentThreshold = 2;

    public static readonly EmojiUsageSnapshot Empty = new([], new Dictionary<string, int>());

    /// <summary>Primary aliases of recently committed emoji, most recent first, without duplicates.</summary>
    public IReadOnlyList<string> RecentAliases { get; } = recentAliases;

    /// <summary>Primary alias to number of commits.</summary>
    public IReadOnlyDictionary<string, int> Frequency { get; } = frequency;

    /// <summary>
    /// Whether an alias is recent or frequently used. Favorites lead their relevance tier in the
    /// matcher without jumping ahead of a stronger tier.
    /// </summary>
    public bool IsFavorite(string alias)
    {
        var key = alias.ToLowerInvariant();
        return Frequency.GetValueOrDefault(key) >= FrequentThreshold || RecentAliases.Contains(key);
    }
}
