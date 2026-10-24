using System.Globalization;
using System.Text.Json;

namespace Cotabby.Core.History;

/// <summary>The file is not a Cotypist user_inputs export.</summary>
public sealed class CotypistImportException()
    : Exception("This file isn't a Cotypist user_inputs export (a JSON list of typing-history rows).");

/// <summary>
/// Converts a Cotypist typing history export into history records.
/// <para>
/// The user's decrypted Cotypist copy is a JSON array of user_inputs rows (textUpToCursor,
/// textAfterCursor, appBundleIdentifier, domain, createdAt and more). Cotypist stores a new row
/// each time it snapshots a field, so one email can appear dozens of times as it grows. Only the
/// most complete version of each piece of writing is kept. Rows that share an app and an opening
/// are candidates for being the same writing, but a shorter one is only dropped when nearly all of
/// its words appear in a longer one, so two messages that open the same way both stay.
/// </para>
/// </summary>
public static class CotypistExportImporter
{
    private sealed record Row(
        string? CreatedAt, string? UpdatedAt, string? AppBundleIdentifier,
        string? TextUpToCursor, string? TextAfterCursor, string? Domain);

    /// <summary>Texts shorter than this after scrubbing are fragments with nothing to learn.</summary>
    public const int MinimumCharacters = 20;
    private const int GroupingPrefixLength = 40;

    /// <summary>Share of a shorter text's words that must appear in a longer one for it to be an earlier snapshot.</summary>
    private const double SnapshotWordShare = 0.8;

    /// <summary>Placeholder for rows Cotypist could not attribute to an app.</summary>
    public const string UnknownBundleIdentifier = "unknown";

    /// <summary>
    /// SQLite-style timestamps in UTC ("2026-06-05 16:23:53.028"), with ISO 8601 forms accepted too.
    /// </summary>
    private static readonly string[] DateFormats =
    [
        "yyyy-MM-dd HH:mm:ss.FFFFFFF", "yyyy-MM-dd HH:mm:ss",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK", "yyyy-MM-dd'T'HH:mm:ssK",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz", "yyyy-MM-dd'T'HH:mm:sszzz",
    ];

    public static List<TypingHistoryRecord> RecordsFromExport(byte[] data)
    {
        var rows = ParseRows(data);
        if (rows.Count > 0 && !rows.Any(row => row.TextUpToCursor is not null)) throw new CotypistImportException();

        var groups = new Dictionary<string, List<TypingHistoryRecord>>();
        foreach (var row in rows)
        {
            var (text, typedLength) = TypingHistoryScrubber.Scrub(row.TextUpToCursor ?? "", row.TextAfterCursor ?? "");
            if (text.Trim().Length < MinimumCharacters) continue;
            string bundleIdentifier = NormalizedBundleIdentifier(row.AppBundleIdentifier);
            var createdAt = ParseDate(row.CreatedAt) ?? DateTimeOffset.UnixEpoch;
            var record = new TypingHistoryRecord
            {
                Id = Guid.NewGuid(),
                BundleIdentifier = bundleIdentifier,
                Domain = NormalizedDomain(row.Domain),
                CreatedAt = createdAt,
                UpdatedAt = ParseDate(row.UpdatedAt) ?? createdAt,
                Text = text,
                Source = TypingHistorySource.Imported,
                TypedLength = typedLength,
            };
            string key = bundleIdentifier + "\u001F" + text[..Math.Min(GroupingPrefixLength, text.Length)].ToLowerInvariant();
            if (!groups.TryGetValue(key, out var group)) groups[key] = group = [];
            group.Add(record);
        }
        var latest = groups.Values.SelectMany(LatestVersions).ToList();
        return [.. DroppingEarlierSnapshots(latest).OrderBy(record => record.CreatedAt)];
    }

    /// <summary>Reads the export as a list of rows. Anything else is not an export.</summary>
    private static List<Row> ParseRows(byte[] data)
    {
        try
        {
            using var document = JsonDocument.Parse(data);
            if (document.RootElement.ValueKind != JsonValueKind.Array) throw new CotypistImportException();
            var rows = new List<Row>();
            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object) throw new CotypistImportException();
                rows.Add(new Row(
                    StringField(element, "createdAt"), StringField(element, "updatedAt"),
                    StringField(element, "appBundleIdentifier"), StringField(element, "textUpToCursor"),
                    StringField(element, "textAfterCursor"), StringField(element, "domain")));
            }
            return rows;
        }
        catch (JsonException)
        {
            throw new CotypistImportException();
        }
    }

    /// <summary>A string field, or null when absent. A field of another type means another format.</summary>
    private static string? StringField(JsonElement row, string name)
    {
        if (!row.TryGetProperty(name, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Null => null,
            _ => throw new CotypistImportException(),
        };
    }

    /// <summary>
    /// Within one group (same app, same opening), keeps the most complete version of each piece of
    /// writing. Longest first, a text is dropped when nearly all of its words are in a longer kept text.
    /// </summary>
    private static IEnumerable<TypingHistoryRecord> LatestVersions(List<TypingHistoryRecord> group)
    {
        if (group.Count <= 1) return group;
        var kept = new List<(TypingHistoryRecord Record, HashSet<string> Words)>();
        foreach (var record in group.OrderByDescending(record => record.Text.Length))
        {
            var words = TypingHistoryIndex.Terms(record.Text).ToHashSet();
            // A text with no informative words beyond the shared opening says nothing new.
            bool isEarlierSnapshot = kept.Any(longer =>
                words.Count == 0 || (double)words.Count(longer.Words.Contains) / words.Count >= SnapshotWordShare);
            if (!isEarlierSnapshot) kept.Add((record, words));
        }
        return kept.Select(entry => entry.Record);
    }

    /// <summary>
    /// Early snapshots can be shorter than the grouping prefix and land in their own group. Within
    /// each app, a text that is the start of a longer kept text is the same writing caught early.
    /// </summary>
    private static List<TypingHistoryRecord> DroppingEarlierSnapshots(List<TypingHistoryRecord> records)
    {
        var kept = new List<TypingHistoryRecord>();
        foreach (var app in records.GroupBy(record => record.BundleIdentifier))
        {
            var keptInApp = new List<TypingHistoryRecord>();
            foreach (var record in app.OrderByDescending(record => record.Text.Length))
            {
                if (!keptInApp.Any(longer => longer.Text.StartsWith(record.Text, StringComparison.Ordinal)))
                {
                    keptInApp.Add(record);
                }
            }
            kept.AddRange(keptInApp);
        }
        return kept;
    }

    private static string NormalizedBundleIdentifier(string? identifier)
    {
        string trimmed = identifier?.Trim() ?? "";
        return trimmed.Length == 0 || trimmed == "unknown.bundle" ? UnknownBundleIdentifier : trimmed;
    }

    /// <summary>Cotypist uses "-" for fields with no site.</summary>
    private static string? NormalizedDomain(string? domain)
    {
        string trimmed = domain?.Trim() ?? "";
        return trimmed.Length == 0 || trimmed == "-" ? null : trimmed;
    }

    private static DateTimeOffset? ParseDate(string? text) =>
        text is not null && DateTimeOffset.TryParseExact(
            text, DateFormats, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date)
            ? date
            : null;
}
