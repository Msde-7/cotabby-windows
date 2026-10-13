using System.Text.Json;

namespace Cotabby.Core.Emoji;

/// <summary>
/// The emoji dataset with a precomputed lowercased search index, so query time work stays
/// proportional to the number of records. <c>Resources/Emoji/emoji.json</c> is generated from
/// GitHub's gemoji dataset (MIT licensed).
/// </summary>
/// <remarks>
/// Every search token in the bundled dataset is precomposed BMP text, so its UTF-16 length equals
/// its grapheme count and the matcher can rank by <see cref="string.Length"/>.
/// </remarks>
public sealed class EmojiCatalog
{
    /// <summary>An entry paired with its lowercased searchable tokens.</summary>
    public sealed record IndexedEntry(
        EmojiEntry Entry,
        IReadOnlyList<string> LowerAliases,
        IReadOnlyList<string> LowerKeywords,
        string LowerName);

    private const string ResourceName = "Cotabby.Core.Resources.Emoji.emoji.json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly Lazy<EmojiCatalog> BundledCatalog = new(() =>
    {
        using var stream = typeof(EmojiCatalog).Assembly.GetManifestResourceStream(ResourceName);
        return FromJson(stream);
    });

    // Lowercased alias to first catalog index. The first occurrence wins on an alias collision.
    private readonly Dictionary<string, int> _aliasIndex = new();

    public EmojiCatalog(IEnumerable<EmojiEntry> entries)
    {
        Indexed = entries
            .Select(entry => new IndexedEntry(
                entry,
                entry.Aliases.Select(alias => alias.ToLowerInvariant()).ToArray(),
                entry.Keywords.Select(keyword => keyword.ToLowerInvariant()).ToArray(),
                entry.Name.ToLowerInvariant()))
            .ToArray();

        for (int index = 0; index < Indexed.Count; index++)
        {
            foreach (var alias in Indexed[index].LowerAliases) _aliasIndex.TryAdd(alias, index);
        }
    }

    public IReadOnlyList<IndexedEntry> Indexed { get; }

    /// <summary>The dataset embedded in this assembly, decoded on first use.</summary>
    public static EmojiCatalog Bundled => BundledCatalog.Value;

    /// <summary>The entry whose alias matches case insensitively, or null.</summary>
    public EmojiEntry? EntryForAlias(string alias) =>
        _aliasIndex.TryGetValue(alias.ToLowerInvariant(), out var index) ? Indexed[index].Entry : null;

    /// <summary>
    /// Decodes a dataset. A missing or undecodable resource yields an empty catalog, which disables
    /// the picker instead of taking down the app.
    /// </summary>
    public static EmojiCatalog FromJson(Stream? json)
    {
        if (json is null) return new EmojiCatalog([]);
        try
        {
            return new EmojiCatalog(JsonSerializer.Deserialize<List<EmojiEntry>>(json, JsonOptions) ?? []);
        }
        catch (JsonException)
        {
            return new EmojiCatalog([]);
        }
    }
}
