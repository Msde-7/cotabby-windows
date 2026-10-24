using Cotabby.Core.Focus;
using Cotabby.Core.Suggestions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cotabby.Core.History;

/// <summary>Whether the archive is open.</summary>
public enum TypingHistoryStatus
{
    Loading,
    Ready,

    /// <summary>
    /// The archive exists but could not be opened. Recording and import stay off so the unreadable
    /// file is never overwritten with a smaller one.
    /// </summary>
    Unavailable,
}

/// <summary>
/// Owns the user's typing history: the encrypted archive, recording new typing, and the search
/// structures that turn history into prompt examples and phrase shortcuts.
/// <para>
/// Built once by the app host. The coordinator records through <see cref="ITypingHistoryObserver"/>
/// and reads through <see cref="ISuggestionHistoryProvider"/>, and the settings pane drives it
/// directly. Preferences are kept in the app settings and pushed in with
/// <see cref="ApplyPreferences"/>.
/// </para>
/// <para>
/// Callers arrive from the UI thread and the thread pool, so mutable state sits behind one lock.
/// Decrypting, encrypting and building the index run on the thread pool against copies. Each
/// rebuild carries a generation so a slow, older build never replaces a newer one.
/// </para>
/// </summary>
public sealed class TypingHistoryStore : ISuggestionHistoryProvider, ITypingHistoryObserver
{
    /// <summary>Oldest records are dropped past this many, so rebuilds stay quick.</summary>
    public const int MaximumRecords = 20_000;

    /// <summary>Fields with less text than this are not worth keeping ("ok", a search term).</summary>
    public const int MinimumRecordedCharacters = 20;

    /// <summary>
    /// How much text before the caret focus reading keeps. A field this long was cut to a window
    /// that slides as the user types. Matches the focus tracker's limit.
    /// </summary>
    public const int CaptureWindowChars = 14_000;

    private const int MaximumRecentRecordings = 64;
    private static readonly char[] LineBreaks = ['\n', '\r'];

    private readonly Lock _lock = new();
    private readonly TypingHistoryVault _vault;

    /// <summary>Every write and delete goes through this, so a deletion is never undone by a running save.</summary>
    private readonly TypingHistoryWriter _writer;
    private readonly TimeSpan _saveDelay;
    private readonly ILogger _logger;

    private TypingHistoryPreferences _preferences = TypingHistoryPreferences.Defaults;
    private TypingHistoryStatus _status = TypingHistoryStatus.Loading;
    private string? _unavailableMessage;
    private bool _isImporting;
    private string? _lastImportMessage;
    private string? _deletionError;

    /// <summary>Bumped by Delete All. Work started before it discards its result.</summary>
    private int _persistenceGeneration;

    /// <summary>Numbers each captured save so an older snapshot never overwrites a newer one.</summary>
    private int _saveSequence;

    /// <summary>
    /// Counts changes to the records. While it matches the count the last finished write covered,
    /// saves are skipped, so a user who never records gets no archive or key. A write still in
    /// progress does not count, so the exit flush writes again.
    /// </summary>
    private int _changeCount;
    private int _savedChangeCount;

    private List<TypingHistoryRecord> _records = [];
    private TypingHistoryIndex? _index;
    private TypingHistoryPhrasePredictor? _phrases;
    private int _rebuildGeneration;
    private CancellationTokenSource? _saveCancellation;
    private ActiveRecording? _active;

    /// <summary>
    /// Recently finished fields worth keeping, by field key. Coming back to one that still holds
    /// that text resumes the same record instead of starting a duplicate.
    /// </summary>
    private readonly Dictionary<string, ActiveRecording> _recent = [];

    /// <summary>Ranked example candidates for the current query block.</summary>
    private (string Key, IReadOnlyList<TypingHistoryIndex.Candidate> Candidates)? _exampleCache;

    /// <summary>
    /// The field being typed in now. Its raw text is only scrubbed and copied into the records when
    /// saving or when focus moves on, so recording costs a string comparison per keystroke.
    /// </summary>
    private sealed record ActiveRecording
    {
        public required string FieldKey { get; init; }
        public required Guid RecordId { get; init; }
        public required string BundleIdentifier { get; init; }
        public string? Domain { get; init; }
        public required DateTimeOffset CreatedAt { get; init; }
        public required string RawText { get; set; }

        /// <summary>Characters before the caret in <see cref="RawText"/> at the latest capture.</summary>
        public required int RawTypedLength { get; set; }

        /// <summary>For a long document a changed title tells another document from a caret jump.</summary>
        public string WindowTitle { get; set; } = "";
    }

    /// <param name="loadsArchive">False starts empty and ready without opening the archive.</param>
    /// <param name="saveDelay">How long typing must pause before a background save.</param>
    public TypingHistoryStore(
        TypingHistoryVault vault, TypingHistoryPreferences? preferences = null, bool loadsArchive = true,
        TimeSpan? saveDelay = null, ILogger? logger = null)
    {
        _vault = vault;
        _writer = new TypingHistoryWriter(vault);
        _preferences = preferences ?? TypingHistoryPreferences.Defaults;
        _saveDelay = saveDelay ?? TimeSpan.FromSeconds(5);
        _logger = logger ?? NullLogger.Instance;
        if (loadsArchive) _ = LoadArchiveAsync();
        else _status = TypingHistoryStatus.Ready;
    }

    /// <summary>Raised on any thread when something the settings pane shows changed.</summary>
    public event Action? Changed;

    public TypingHistoryPreferences Preferences { get { lock (_lock) return _preferences; } }
    public int RecordCount { get { lock (_lock) return _records.Count; } }
    public TypingHistoryStatus Status { get { lock (_lock) return _status; } }
    public string? UnavailableMessage { get { lock (_lock) return _unavailableMessage; } }
    public bool IsImporting { get { lock (_lock) return _isImporting; } }
    public string? LastImportMessage { get { lock (_lock) return _lastImportMessage; } }

    /// <summary>Why the last Delete All failed, until one succeeds. The history is still stored meanwhile.</summary>
    public string? DeletionError { get { lock (_lock) return _deletionError; } }

    // Preferences

    /// <summary>Brings the store in line with preferences saved elsewhere, such as after a reset.</summary>
    public void ApplyPreferences(TypingHistoryPreferences preferences)
    {
        SetUsingHistory(preferences.IsUsingHistory);
        SetRecording(preferences.IsRecording);
        var current = Preferences.ExcludedApps;
        foreach (var app in current.Except(preferences.ExcludedApps, StringComparer.OrdinalIgnoreCase).ToList())
        {
            SetExcluded(app, excluded: false);
        }
        foreach (var app in preferences.ExcludedApps.Except(current, StringComparer.OrdinalIgnoreCase).ToList())
        {
            SetExcluded(app, excluded: true);
        }
    }

    public void SetUsingHistory(bool enabled)
    {
        lock (_lock)
        {
            if (_preferences.IsUsingHistory == enabled) return;
            _preferences = _preferences with { IsUsingHistory = enabled };
            _exampleCache = null;
        }
        RaiseChanged();
    }

    public void SetRecording(bool enabled)
    {
        lock (_lock)
        {
            if (_preferences.IsRecording == enabled) return;
            _preferences = _preferences with { IsRecording = enabled };
            if (!enabled) FinishActiveRecording();
        }
        RaiseChanged();
    }

    public void SetExcluded(string app, bool excluded)
    {
        lock (_lock)
        {
            var apps = _preferences.ExcludedApps.Where(a => !string.Equals(a, app, StringComparison.OrdinalIgnoreCase)).ToList();
            if (excluded) apps.Add(app);
            apps.Sort(StringComparer.OrdinalIgnoreCase);
            if (apps.SequenceEqual(_preferences.ExcludedApps)) return;
            _preferences = _preferences with { ExcludedApps = apps };
            if (excluded) ForgetApp(app);
        }
        RaiseChanged();
    }

    /// <summary>
    /// Excluding an app mid-field discards that field's text instead of keeping it, including any
    /// part a background save already copied into the records.
    /// </summary>
    private void ForgetApp(string app)
    {
        foreach (var key in _recent.Where(pair => SameApp(pair.Value.BundleIdentifier, app)).Select(pair => pair.Key).ToList())
        {
            _recent.Remove(key);
        }
        if (_active is not { } active || !SameApp(active.BundleIdentifier, app)) return;
        _active = null;
        int index = _records.FindIndex(record => record.Id == active.RecordId);
        if (index < 0) return;
        _records.RemoveAt(index);
        _changeCount++;
        ScheduleSave();
        RebuildSearchStructures();
    }

    private static bool SameApp(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    // Loading, saving, rebuilding

    public async Task LoadArchiveAsync()
    {
        int generation;
        lock (_lock) generation = _persistenceGeneration;
        try
        {
            var loaded = await Task.Run(_vault.Load).ConfigureAwait(false);
            lock (_lock)
            {
                // Delete All ran while the archive was decrypting: the loaded records are gone now.
                if (generation != _persistenceGeneration) return;
                _records = loaded;
                _savedChangeCount = _changeCount;
                _status = TypingHistoryStatus.Ready;
                RebuildSearchStructures();
            }
        }
        catch (Exception ex)
        {
            lock (_lock)
            {
                _status = TypingHistoryStatus.Unavailable;
                _unavailableMessage = "Typing history couldn't be opened, so it's paused to avoid overwriting it.";
            }
            _logger.LogError(ex, "Typing history archive could not be loaded.");
        }
        RaiseChanged();
    }

    /// <summary>
    /// Writes now, on the calling thread. Used at exit, when there is no time left for a
    /// debounced background save. Writes nothing when nothing changed.
    /// </summary>
    public void Flush()
    {
        PendingSave? save;
        lock (_lock)
        {
            if (_status != TypingHistoryStatus.Ready) return;
            _saveCancellation?.Cancel();
            MaterializeActiveRecording();
            save = CaptureSave();
        }
        if (save is null) return;
        Write(save);
    }

    private void ScheduleSave()
    {
        if (_status != TypingHistoryStatus.Ready) return;
        _saveCancellation?.Cancel();
        var cancellation = _saveCancellation = new CancellationTokenSource();
        var delay = _saveDelay;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, cancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            PendingSave? save;
            lock (_lock)
            {
                if (cancellation.IsCancellationRequested) return;
                if (MaterializeActiveRecording()) RaiseChangedLater();
                save = CaptureSave();
            }
            if (save is not null) Write(save);
        });
    }

    private void Write(PendingSave save)
    {
        try
        {
            _writer.Save(save.Records, save.Generation, save.Sequence);
            // Only now are these changes on disk. Until here an exit flushes them again.
            lock (_lock) _savedChangeCount = Math.Max(_savedChangeCount, save.ChangeCount);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Typing history could not be saved.");
        }
    }

    /// <summary>A numbered copy of the records for the writer, tagged with the deletion generation.</summary>
    private sealed record PendingSave(List<TypingHistoryRecord> Records, int Generation, int Sequence, int ChangeCount);

    /// <summary>Snapshots the records, or returns null when a finished write already holds them.</summary>
    private PendingSave? CaptureSave()
    {
        if (_changeCount <= _savedChangeCount) return null;
        _saveSequence++;
        return new PendingSave([.. _records], _persistenceGeneration, _saveSequence, _changeCount);
    }

    /// <summary>
    /// Rebuilds the index and phrase table on the thread pool from a copy. The field being typed in
    /// is left out so an unfinished draft never becomes its own example.
    /// </summary>
    private void RebuildSearchStructures()
    {
        int generation = ++_rebuildGeneration;
        var activeId = _active?.RecordId;
        var snapshot = _records.Where(record => record.Id != activeId).ToList();
        _ = Task.Run(() =>
        {
            var index = new TypingHistoryIndex(snapshot);
            var phrases = new TypingHistoryPhrasePredictor(snapshot);
            lock (_lock)
            {
                if (generation != _rebuildGeneration) return;
                _index = index;
                _phrases = phrases;
                _exampleCache = null;
            }
        });
    }

    // Recording

    /// <summary>
    /// Called with each fresh snapshot of the focused field. Cheap unless the field's text changed.
    /// <paramref name="isAllowed"/> carries Cotabby's own availability rule, so history is only
    /// recorded where Cotabby is active. It is only evaluated once recording is on and the text
    /// changed. Secure fields and terminals are checked here as well.
    /// </summary>
    public void Observe(FocusedField? field, Func<bool> isAllowed)
    {
        bool changed;
        lock (_lock) changed = ObserveLocked(field, isAllowed);
        if (changed) RaiseChanged();
    }

    private bool ObserveLocked(FocusedField? field, Func<bool> isAllowed)
    {
        int countBefore = _records.Count;
        if (_status != TypingHistoryStatus.Ready || !_preferences.IsRecording
            || field is null || field.IsSecure || IsTerminal(field)
            || _preferences.ExcludedApps.Contains(field.ProcessName, StringComparer.OrdinalIgnoreCase))
        {
            FinishActiveRecording();
            return _records.Count != countBefore;
        }

        // The focus session is left out on purpose: leaving a field and coming back is the same
        // document and should stay one record.
        string fieldKey = $"{field.ProcessName}|{field.ProcessId}|{field.FieldId}";
        string text = field.PrecedingText + field.TrailingText;
        // Unchanged text in the same field is the common case. Answer it before the settings check.
        if (_active is { } same && same.FieldKey == fieldKey && same.RawText == text) return false;
        if (!isAllowed())
        {
            FinishActiveRecording();
            return _records.Count != countBefore;
        }

        int typedLength = field.PrecedingText.Length;
        if (_active is { } active && active.FieldKey == fieldKey && !HoldsNewDocument(active, field, text))
        {
            active.RawText = text;
            active.RawTypedLength = typedLength;
            active.WindowTitle = field.WindowTitle;
            ScheduleSave();
            return false;
        }
        // Another field, or this field now holds different writing (a sent chat message was cleared,
        // or another conversation's draft is showing): keep what was there and start anew.
        FinishActiveRecording();
        _active = ResumedRecording(fieldKey, field, text) ?? new ActiveRecording
        {
            FieldKey = fieldKey,
            RecordId = Guid.NewGuid(),
            BundleIdentifier = field.ProcessName,
            Domain = null,
            CreatedAt = DateTimeOffset.UtcNow,
            RawText = text,
            RawTypedLength = typedLength,
            WindowTitle = field.WindowTitle,
        };
        return _records.Count != countBefore;
    }

    /// <summary>
    /// Whether the field's new text is different writing from what is being recorded.
    /// <para>
    /// Chat apps clear the composer when a message is sent, and some reuse one composer for every
    /// conversation. Typing, deleting or editing one spot leaves most of the text in place between
    /// two observations, while sending or switching replaces it. A long document read through the
    /// capture window is different: the window slides with every keystroke and a caret jump shows
    /// another part, so there only a new window title with nothing of the old window left marks
    /// another document.
    /// </para>
    /// </summary>
    private bool HoldsNewDocument(ActiveRecording active, FocusedField field, string text)
    {
        if (IsWorthKeeping(active.RawText))
        {
            if (field.PrecedingText.Length < CaptureWindowChars) return !KeepsMostOf(active.RawText, text);
            return active.WindowTitle != field.WindowTitle && !SharesContent(active.RawText, text);
        }
        // Nothing worth keeping is being recorded, such as just after a message was sent. If the
        // field shows the writing it held a moment ago again, go back to that record.
        if (!_recent.TryGetValue(active.FieldKey, out var recent) || recent.RecordId == active.RecordId) return false;
        return text.StartsWith(recent.RawText, StringComparison.Ordinal);
    }

    /// <summary>Terminal text is shell commands and output, where secrets are common.</summary>
    private static bool IsTerminal(FocusedField field) =>
        TerminalAppDetector.IsTerminal(field.ProcessName) || TerminalAppDetector.IsIntegratedTerminal(field.ClassName);

    /// <summary>
    /// Continues a recently finished field's record when the user comes back to it, but only while
    /// the field still holds everything that record has. A field id can be reused by a different
    /// field, and resuming into the wrong record would overwrite earlier writing.
    /// </summary>
    private ActiveRecording? ResumedRecording(string fieldKey, FocusedField field, string text)
    {
        if (!_recent.TryGetValue(fieldKey, out var recent) || !text.StartsWith(recent.RawText, StringComparison.Ordinal))
        {
            return null;
        }
        // A copy, so the remembered entry keeps the text it finished with.
        return recent with
        {
            RawText = text,
            RawTypedLength = field.PrecedingText.Length,
            WindowTitle = field.WindowTitle,
        };
    }

    /// <summary>Commits the active field to the records and makes it searchable.</summary>
    private void FinishActiveRecording()
    {
        if (_active is not { } finished) return;
        bool changed = MaterializeActiveRecording();
        // Only writing worth keeping can be resumed. Remembering an emptied field would replace the
        // entry for the text it held before.
        if (IsWorthKeeping(finished.RawText))
        {
            _recent[finished.FieldKey] = finished;
            if (_recent.Count > MaximumRecentRecordings)
            {
                var oldest = _recent.Values.MinBy(recording => recording.CreatedAt)!;
                _recent.Remove(oldest.FieldKey);
            }
        }
        _active = null;
        if (changed)
        {
            ScheduleSave();
            RebuildSearchStructures();
        }
    }

    private static bool IsWorthKeeping(string text) => text.Trim().Length >= MinimumRecordedCharacters;

    /// <summary>
    /// Whether <paramref name="updated"/> keeps at least half of <paramref name="old"/> in place:
    /// their common start plus common end. One edit keeps most of a text. A cleared field or a
    /// different text keeps little.
    /// </summary>
    public static bool KeepsMostOf(string old, string updated)
    {
        int prefix = 0;
        int limit = Math.Min(old.Length, updated.Length);
        while (prefix < limit && old[prefix] == updated[prefix]) prefix++;
        int suffix = 0;
        int suffixLimit = limit - prefix;
        while (suffix < suffixLimit && old[old.Length - 1 - suffix] == updated[updated.Length - 1 - suffix]) suffix++;
        return (prefix + suffix) * 2 >= old.Length;
    }

    /// <summary>
    /// Whether <paramref name="updated"/> still contains a piece from inside <paramref name="old"/>.
    /// Typing in a long document slides the window a little, so pieces from the middle of the old
    /// window are still there. Another document contains none of them.
    /// </summary>
    public static bool SharesContent(string old, string updated)
    {
        const int probeLength = 48;
        if (old.Length <= probeLength * 2) return updated.Contains(old, StringComparison.Ordinal);
        for (int quarter = 1; quarter <= 3; quarter++)
        {
            int start = (old.Length - probeLength) * quarter / 4;
            if (updated.AsSpan().IndexOf(old.AsSpan(start, probeLength), StringComparison.Ordinal) >= 0) return true;
        }
        return false;
    }

    /// <summary>Copies the active field's scrubbed text into the records. Returns whether anything changed.</summary>
    private bool MaterializeActiveRecording()
    {
        if (_active is not { } active) return false;
        int split = Math.Min(active.RawTypedLength, active.RawText.Length);
        var (text, typedLength) = TypingHistoryScrubber.Scrub(active.RawText[..split], active.RawText[split..]);
        int existing = _records.FindIndex(record => record.Id == active.RecordId);
        if (!IsWorthKeeping(text))
        {
            // The user deleted the field's text down to nothing worth keeping.
            if (existing < 0) return false;
            _records.RemoveAt(existing);
            _changeCount++;
            return true;
        }
        if (existing >= 0)
        {
            var record = _records[existing];
            if (record.Text == text && record.TypedLength == typedLength) return false;
            _records[existing] = record with { Text = text, TypedLength = typedLength, UpdatedAt = DateTimeOffset.UtcNow };
        }
        else
        {
            _records.Add(new TypingHistoryRecord
            {
                Id = active.RecordId,
                BundleIdentifier = active.BundleIdentifier,
                Domain = active.Domain,
                CreatedAt = active.CreatedAt,
                UpdatedAt = DateTimeOffset.UtcNow,
                Text = text,
                Source = TypingHistorySource.Recorded,
                TypedLength = typedLength,
            });
            TrimToCapacity();
        }
        _changeCount++;
        return true;
    }

    private void TrimToCapacity()
    {
        if (_records.Count <= MaximumRecords) return;
        _records = [.. _records.OrderBy(record => record.UpdatedAt).Skip(_records.Count - MaximumRecords)];
    }

    // Import and deletion

    /// <summary>
    /// Imports a Cotypist user_inputs.json export. Entries whose text is already in history are
    /// skipped, so importing the same file twice adds nothing.
    /// </summary>
    public async Task ImportCotypistExportAsync(string path)
    {
        int generation;
        lock (_lock)
        {
            if (_status != TypingHistoryStatus.Ready || _isImporting) return;
            _isImporting = true;
            generation = _persistenceGeneration;
        }
        RaiseChanged();
        try
        {
            var imported = await Task.Run(async () =>
                CotypistExportImporter.RecordsFromExport(await File.ReadAllBytesAsync(path).ConfigureAwait(false)))
                .ConfigureAwait(false);
            lock (_lock)
            {
                // Delete All ran while the file was being read. Adding the import would partly undo it.
                if (generation != _persistenceGeneration)
                {
                    _lastImportMessage = "Import cancelled because typing history was deleted.";
                    return;
                }
                var knownTexts = _records.Select(record => record.Text).ToHashSet();
                var fresh = imported.Where(record => !knownTexts.Contains(record.Text)).ToList();
                _lastImportMessage = $"Imported {fresh.Count} entries"
                    + (imported.Count > fresh.Count
                        ? $" ({imported.Count - fresh.Count} were already in your history)."
                        : ".");
                if (fresh.Count == 0) return;
                _records.AddRange(fresh);
                TrimToCapacity();
                _changeCount++;
                ScheduleSave();
                RebuildSearchStructures();
            }
        }
        catch (CotypistImportException ex)
        {
            lock (_lock) _lastImportMessage = ex.Message;
        }
        catch (Exception ex)
        {
            lock (_lock) _lastImportMessage = $"Import failed: {ex.Message}";
        }
        finally
        {
            lock (_lock) _isImporting = false;
            RaiseChanged();
        }
    }

    /// <summary>
    /// Removes every record, the encrypted file and its key. Also the way out of an archive that
    /// can no longer be opened: deleting it lets recording start over.
    /// </summary>
    public void DeleteAll()
    {
        int generation;
        lock (_lock)
        {
            _saveCancellation?.Cancel();
            generation = ++_persistenceGeneration;
        }
        try
        {
            // Waits for any save already writing, then deletes. Saves captured earlier are dropped.
            _writer.Destroy(generation);
        }
        catch (Exception ex)
        {
            // The archive or its key is still on disk. Keep showing what is stored and say why. A save
            // captured before this attempt was dropped as stale and the file may already be gone, so
            // the kept records count as unsaved until written again.
            lock (_lock)
            {
                _changeCount++;
                _deletionError = $"Typing history couldn't be deleted: {ex.Message}";
            }
            _logger.LogError(ex, "Typing history could not be deleted.");
            RaiseChanged();
            return;
        }
        lock (_lock)
        {
            _active = null;
            _recent.Clear();
            _records = [];
            _savedChangeCount = _changeCount;
            _index = null;
            _phrases = null;
            _exampleCache = null;
            _rebuildGeneration++;
            _lastImportMessage = null;
            _deletionError = null;
            _unavailableMessage = null;
            _status = TypingHistoryStatus.Ready;
        }
        RaiseChanged();
    }

    // ISuggestionHistoryProvider

    public IReadOnlyList<string> HistoryExamples(FocusedField field, bool usesEndpoint)
    {
        TypingHistoryIndex? index;
        string precedingText = field.PrecedingText;
        string stableText = TypingHistoryQuery.StableText(precedingText);
        // The window title, such as an email subject, is the most stable topical signal a field has,
        // so it joins the query even before the first full block of words is typed.
        string queryText = $"{stableText} {field.WindowTitle}";
        string cacheKey = $"{field.ProcessName}|{field.ProcessId}|{field.FieldId}|{queryText}";
        IReadOnlyList<TypingHistoryIndex.Candidate>? candidates = null;
        lock (_lock)
        {
            index = _index;
            if (!_preferences.IsUsingHistory || usesEndpoint || index is null) return [];
            if (_exampleCache is { } cache && cache.Key == cacheKey) candidates = cache.Candidates;
        }
        if (candidates is null)
        {
            candidates = index.Candidates(new TypingHistoryQuery(queryText, field.ProcessName, null, precedingText));
            lock (_lock)
            {
                if (ReferenceEquals(_index, index)) _exampleCache = (cacheKey, candidates);
            }
        }
        // The ranking holds for a block of words, but the field grows inside the block, so the check
        // against echoing the user's own draft runs on the live text every time.
        return TypingHistoryIndex.Examples(candidates, precedingText);
    }

    public string? PhraseContinuation(SuggestionRequest request, bool usesEndpoint)
    {
        TypingHistoryPhrasePredictor? phrases;
        lock (_lock)
        {
            phrases = _phrases;
            if (!_preferences.IsUsingHistory || usesEndpoint || phrases is null) return null;
        }
        // Text after the caret on the same line would be pushed along by an inserted phrase. Those
        // mid-line positions are left to the model, which sees the trailing text.
        int lineEnd = request.Suffix.IndexOfAny(LineBreaks);
        string restOfLine = lineEnd < 0 ? request.Suffix : request.Suffix[..lineEnd];
        if (restOfLine.Trim(' ', '\t').Length > 0) return null;
        return phrases.Continuation(
            request.Prefix,
            new TypingHistoryPhrasePredictor.Limits(request.WordRange?.HighWords ?? 8, !request.SingleLine));
    }

    private void RaiseChanged() => Changed?.Invoke();

    /// <summary>Raises <see cref="Changed"/> once the caller has released the lock.</summary>
    private void RaiseChangedLater() => _ = Task.Run(RaiseChanged);
}
