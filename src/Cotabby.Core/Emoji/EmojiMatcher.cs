namespace Cotabby.Core.Emoji;

/// <summary>
/// Ranks emoji against a typed query. Pure, so the same query, catalog and usage snapshot always
/// produce the same ordered results.
/// </summary>
/// <remarks>
/// Relevance tiers, lower is better.
/// <list type="bullet">
///   <item>0 exact alias</item>
///   <item>1 alias prefix, or a curated synonym whose key the query equals</item>
///   <item>2 keyword or name prefix, or a synonym whose key the query is a prefix of</item>
///   <item>3 alias substring</item>
///   <item>4 keyword or name substring</item>
///   <item>5 fuzzy typo match, only when stronger results are sparse</item>
/// </list>
/// Within a tier the order is personal favorites, then the shorter matched token, then the curated
/// popularity prior, then catalog order. Favorites and popularity never lift an emoji past a
/// stronger tier.
/// </remarks>
public sealed class EmojiMatcher(EmojiCatalog catalog)
{
    /// <summary>Default number of matches the panel asks for.</summary>
    public const int DefaultLimit = 24;

    // Shorter queries would pull in loosely related typo candidates.
    private const int MinFuzzyQueryLength = 3;

    // How much longer than the query a subsequence candidate may be.
    private const int MaxSubsequenceGap = 4;

    private const int TierExactAlias = 0;
    private const int TierAliasPrefix = 1;
    private const int TierSynonymExact = 1;
    private const int TierKeywordOrNamePrefix = 2;
    private const int TierSynonymPrefix = 2;
    private const int TierAliasSubstring = 3;
    private const int TierKeywordOrNameSubstring = 4;
    private const int TierFuzzy = 5;

    private readonly record struct Hit(int Tier, int TokenLength);

    private readonly record struct ScoredMatch(
        EmojiMatch Match,
        int Tier,
        int FavoriteBucket,
        int TokenLength,
        int PopularityRank,
        int CatalogIndex);

    public IReadOnlyList<EmojiMatch> Matches(
        string rawQuery,
        EmojiUsageSnapshot? usage = null,
        int limit = DefaultLimit)
    {
        var query = rawQuery.ToLowerInvariant().Trim();
        if (query.Length == 0 || limit <= 0) return [];
        usage ??= EmojiUsageSnapshot.Empty;

        var synonyms = EmojiSynonymCatalog.BoostedAliases(query);
        var indexed = catalog.Indexed;
        var scored = new List<ScoredMatch>();
        var matchedIndices = new HashSet<int>();
        for (int index = 0; index < indexed.Count; index++)
        {
            if (LexicalHit(query, synonyms, indexed[index]) is not { } hit) continue;
            matchedIndices.Add(index);
            scored.Add(MakeScored(indexed[index], index, hit, usage));
        }

        // Typos still resolve when lexical results are sparse, without polluting strong result sets.
        if (scored.Count < limit && query.Length >= MinFuzzyQueryLength)
        {
            for (int index = 0; index < indexed.Count; index++)
            {
                if (matchedIndices.Contains(index)) continue;
                if (FuzzyHit(query, indexed[index]) is not { } hit) continue;
                scored.Add(MakeScored(indexed[index], index, hit, usage));
            }
        }

        scored.Sort(Compare);
        return scored.Take(limit).Select(candidate => candidate.Match).ToArray();
    }

    /// <summary>Suggestions for a bare colon. The user's recents come first, padded with popular emoji.</summary>
    public IReadOnlyList<EmojiMatch> Recents(EmojiUsageSnapshot usage, int limit = DefaultLimit) =>
        EmojiRecents.Suggestions(usage, catalog, limit);

    private static ScoredMatch MakeScored(
        EmojiCatalog.IndexedEntry indexed,
        int index,
        Hit hit,
        EmojiUsageSnapshot usage)
    {
        var primaryAlias = indexed.LowerAliases.Count > 0 ? indexed.LowerAliases[0] : indexed.LowerName;
        return new ScoredMatch(
            new EmojiMatch(indexed.Entry),
            hit.Tier,
            usage.IsFavorite(primaryAlias) ? 0 : 1,
            hit.TokenLength,
            EmojiPopularity.Rank(primaryAlias),
            index);
    }

    private static int Compare(ScoredMatch lhs, ScoredMatch rhs)
    {
        if (lhs.Tier != rhs.Tier) return lhs.Tier.CompareTo(rhs.Tier);
        if (lhs.FavoriteBucket != rhs.FavoriteBucket) return lhs.FavoriteBucket.CompareTo(rhs.FavoriteBucket);
        if (lhs.TokenLength != rhs.TokenLength) return lhs.TokenLength.CompareTo(rhs.TokenLength);
        if (lhs.PopularityRank != rhs.PopularityRank) return lhs.PopularityRank.CompareTo(rhs.PopularityRank);
        return lhs.CatalogIndex.CompareTo(rhs.CatalogIndex);
    }

    private static Hit? LexicalHit(
        string query,
        (HashSet<string> Exact, HashSet<string> Prefix) synonyms,
        EmojiCatalog.IndexedEntry indexed)
    {
        Hit? best = null;
        foreach (var alias in indexed.LowerAliases)
        {
            best = Better(best, AliasHit(query, alias));
            best = Better(best, SynonymHit(synonyms, alias));
        }
        foreach (var keyword in indexed.LowerKeywords)
        {
            best = Better(best, KeywordOrNameHit(query, keyword));
        }
        return Better(best, KeywordOrNameHit(query, indexed.LowerName));
    }

    // The lower tier wins, then the shorter matched token.
    private static Hit? Better(Hit? current, Hit? candidate)
    {
        if (candidate is not { } next) return current;
        if (current is not { } held) return next;
        if (next.Tier < held.Tier) return next;
        if (next.Tier == held.Tier && next.TokenLength < held.TokenLength) return next;
        return held;
    }

    private static Hit? AliasHit(string query, string alias)
    {
        if (alias == query) return new Hit(TierExactAlias, alias.Length);
        if (alias.StartsWith(query, StringComparison.Ordinal)) return new Hit(TierAliasPrefix, alias.Length);
        if (alias.Contains(query, StringComparison.Ordinal)) return new Hit(TierAliasSubstring, alias.Length);
        return null;
    }

    // Token length 0 lets an intent match lead its tier ahead of literal prefixes.
    private static Hit? SynonymHit((HashSet<string> Exact, HashSet<string> Prefix) synonyms, string alias)
    {
        if (synonyms.Exact.Contains(alias)) return new Hit(TierSynonymExact, 0);
        if (synonyms.Prefix.Contains(alias)) return new Hit(TierSynonymPrefix, 0);
        return null;
    }

    private static Hit? KeywordOrNameHit(string query, string token)
    {
        if (token.StartsWith(query, StringComparison.Ordinal)) return new Hit(TierKeywordOrNamePrefix, token.Length);
        if (token.Contains(query, StringComparison.Ordinal)) return new Hit(TierKeywordOrNameSubstring, token.Length);
        return null;
    }

    private static Hit? FuzzyHit(string query, EmojiCatalog.IndexedEntry indexed)
    {
        int? bestLength = null;
        foreach (var candidate in indexed.LowerAliases.Append(indexed.LowerName))
        {
            if (IsFuzzyMatch(query, candidate)) bestLength = Math.Min(bestLength ?? int.MaxValue, candidate.Length);
        }
        return bestLength is { } length ? new Hit(TierFuzzy, length) : null;
    }

    /// <summary>
    /// A fuzzy match is a bounded edit distance (transpositions and single typos, "recieve" for
    /// "receive") or a length capped subsequence (dropped letters, "thnk" for "thinking").
    /// </summary>
    private static bool IsFuzzyMatch(string query, string candidate)
    {
        int bound = query.Length <= 4 ? 1 : 2;
        if (Math.Abs(candidate.Length - query.Length) <= bound && OsaDistance(query, candidate) <= bound)
        {
            return true;
        }
        return candidate.Length <= query.Length + MaxSubsequenceGap && IsSubsequence(query, candidate);
    }

    private static bool IsSubsequence(string needle, string haystack)
    {
        int matched = 0;
        foreach (char character in haystack)
        {
            if (character != needle[matched]) continue;
            matched++;
            if (matched == needle.Length) return true;
        }
        return false;
    }

    /// <summary>Optimal string alignment distance, Levenshtein plus adjacent transpositions as one edit.</summary>
    private static int OsaDistance(string source, string target)
    {
        if (source.Length == 0) return target.Length;
        if (target.Length == 0) return source.Length;

        var distance = new int[source.Length + 1, target.Length + 1];
        for (int row = 0; row <= source.Length; row++) distance[row, 0] = row;
        for (int column = 0; column <= target.Length; column++) distance[0, column] = column;

        for (int row = 1; row <= source.Length; row++)
        {
            for (int column = 1; column <= target.Length; column++)
            {
                int cost = source[row - 1] == target[column - 1] ? 0 : 1;
                int value = Math.Min(
                    Math.Min(distance[row - 1, column] + 1, distance[row, column - 1] + 1),
                    distance[row - 1, column - 1] + cost);
                if (row > 1 && column > 1
                    && source[row - 1] == target[column - 2]
                    && source[row - 2] == target[column - 1])
                {
                    value = Math.Min(value, distance[row - 2, column - 2] + 1);
                }
                distance[row, column] = value;
            }
        }
        return distance[source.Length, target.Length];
    }
}
