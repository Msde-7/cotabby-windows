using System.Text.Json.Serialization;
using Cotabby.Core.Focus;
using Cotabby.Core.Suggestions;

namespace Cotabby.Core.History;

/// <summary>Where a history record came from.</summary>
public enum TypingHistorySource
{
    /// <summary>Captured by Cotabby while the user typed.</summary>
    Recorded,

    /// <summary>Brought in from another app's export, such as Cotypist.</summary>
    Imported,
}

/// <summary>
/// One field's worth of the user's writing. A record is updated while the user keeps typing in the
/// same field, so a long email is one record. <see cref="Text"/> is already scrubbed of secret-like
/// tokens by <see cref="TypingHistoryScrubber"/>.
/// </summary>
public sealed record TypingHistoryRecord
{
    public required Guid Id { get; init; }

    /// <summary>The app the text was written in. On Windows this is the process name.</summary>
    public required string BundleIdentifier { get; init; }

    /// <summary>Web domain for browser fields, or null.</summary>
    public string? Domain { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
    public required string Text { get; init; }
    public required TypingHistorySource Source { get; init; }

    /// <summary>
    /// How many UTF-16 units at the start of <see cref="Text"/> were before the caret. Text after
    /// the caret is often someone else's, such as a quoted email thread, so only this part is
    /// learned from. Null means the whole text counts.
    /// </summary>
    public int? TypedLength { get; init; }

    /// <summary>The part of <see cref="Text"/> the user wrote themselves.</summary>
    [JsonIgnore]
    public string TypedText =>
        TypedLength is { } length && length < Text.Length ? Text[..Math.Max(0, length)] : Text;
}

/// <summary>The encrypted file's plaintext. Versioned so a format change can migrate.</summary>
public sealed record TypingHistoryArchive
{
    public const int CurrentVersion = 1;

    public int Version { get; init; } = CurrentVersion;
    public List<TypingHistoryRecord> Records { get; init; } = [];
}

/// <summary>The user's typing history preferences. Both switches are off by default.</summary>
public sealed record TypingHistoryPreferences(
    bool IsUsingHistory,
    bool IsRecording,
    IReadOnlyList<string> ExcludedApps)
{
    public static TypingHistoryPreferences Defaults { get; } = new(false, false, []);

    public bool Equals(TypingHistoryPreferences? other) =>
        other is not null && IsUsingHistory == other.IsUsingHistory && IsRecording == other.IsRecording
        && ExcludedApps.SequenceEqual(other.ExcludedApps);

    public override int GetHashCode() => HashCode.Combine(IsUsingHistory, IsRecording, ExcludedApps.Count);
}

/// <summary>
/// Read-only access to typing history for the suggestion pipeline. Both answers are empty for the
/// endpoint engine, because history never leaves this PC, and while history is turned off.
/// </summary>
public interface ISuggestionHistoryProvider
{
    /// <summary>Short passages of the user's past writing that resemble this field, best first.</summary>
    IReadOnlyList<string> HistoryExamples(FocusedField field, bool usesEndpoint);

    /// <summary>Exact text to insert when history is confident how the phrase ends, else null.</summary>
    string? PhraseContinuation(SuggestionRequest request, bool usesEndpoint);
}

/// <summary>Receives focused field snapshots so typing can be recorded.</summary>
public interface ITypingHistoryObserver
{
    /// <summary>
    /// Called with each fresh snapshot, or null when focus left every supported field.
    /// <paramref name="isAllowed"/> is Cotabby's own availability rule for the field.
    /// </summary>
    void Observe(FocusedField? field, Func<bool> isAllowed);
}
