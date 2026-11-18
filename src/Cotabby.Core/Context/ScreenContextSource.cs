using System.Text.RegularExpressions;
using Cotabby.Core.Focus;
using Cotabby.Core.Text;
using Microsoft.Extensions.Logging;

namespace Cotabby.Core.Context;

/// <summary>What a screen read found: the recognized lines, where the field sat, and the window title.</summary>
/// <param name="FocusBounds">The field in the capture's unit square, origin top left, or null.</param>
public sealed record ScreenCapture(IReadOnlyList<OcrLine> Lines, OcrBounds? FocusBounds, string? WindowTitle);

/// <summary>Captures the window around a field and recognizes its text.</summary>
public interface IScreenTextReader
{
    /// <summary>Null when nothing could be captured, such as when the field's window is no longer in front.</summary>
    Task<ScreenCapture?> ReadAsync(FocusedField field, VisualContextConfiguration configuration, CancellationToken ct);
}

/// <summary>
/// Keeps one screen-text excerpt for the focused field. A capture starts once focus settles,
/// refreshes on a timer while the same field stays focused, and expires so an old screen never
/// stands in for the current one. Capture and recognition never hold up a suggestion: a request
/// takes whatever excerpt is ready.
/// </summary>
/// <remarks>
/// Upstream refreshes every 3 seconds for as long as the field has focus. Each refresh here reads
/// the field and copies the window, and recognizes it again when its pixels changed, which costs a
/// few percent of a core with nobody typing. So once the field's text and caret have not moved for
/// a while the refresh slows down, and the next edit seen in a request or a refresh brings the
/// fast pace back at once, with a capture straight away.
/// </remarks>
public sealed partial class ScreenContextSource : IDisposable
{
    private readonly IScreenTextReader _reader;
    private readonly Func<FocusedField?> _currentField;
    private readonly TimeProvider _clock;
    private readonly ILogger _logger;
    private readonly TimeSpan _settle;
    private readonly TimeSpan _refreshInterval;
    private readonly TimeSpan _idleRefreshInterval;
    private readonly TimeSpan _activeWindow;
    private readonly TimeSpan _lifetime;
    private readonly object _gate = new();

    private Session? _session;

    private sealed class Session(string identity, VisualContextConfiguration configuration)
    {
        public string Identity { get; } = identity;
        public VisualContextConfiguration Configuration { get; } = configuration;
        public CancellationTokenSource Cts { get; } = new();
        public string? Excerpt;
        public long CapturedAt;

        /// <summary>
        /// How long the excerpt counts as current: the lifetime at the usual pace, stretched by as
        /// much as a slow wait is longer, so it lasts until the refresh after the next one is due.
        /// </summary>
        public TimeSpan ValidFor;

        /// <summary>The field as last seen, and when its text or caret last moved.</summary>
        public string? FieldText;
        public int FieldCaret;
        public long EditedAt;

        /// <summary>Set to end a slow wait early. Null while the refresh runs at its usual pace.</summary>
        public TaskCompletionSource? Wake;

        /// <summary>The last text read, kept through a failed capture so reading it again is no change.</summary>
        public string? LastText;
    }

    public ScreenContextSource(
        IScreenTextReader reader, Func<FocusedField?> currentField, ILogger logger, TimeProvider? clock = null,
        TimeSpan? settle = null, TimeSpan? refreshInterval = null, TimeSpan? lifetime = null,
        TimeSpan? idleRefreshInterval = null, TimeSpan? activeWindow = null)
    {
        _reader = reader;
        _currentField = currentField;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
        _settle = settle ?? TimeSpan.FromMilliseconds(250);
        _refreshInterval = refreshInterval ?? TimeSpan.FromSeconds(3);
        _lifetime = lifetime ?? TimeSpan.FromSeconds(6);
        _idleRefreshInterval = idleRefreshInterval ?? TimeSpan.FromSeconds(10);
        _activeWindow = activeWindow ?? TimeSpan.FromSeconds(30);
    }

    /// <summary>Raised on the thread pool the first time a field's excerpt becomes ready.</summary>
    public event Action<FocusedField>? FirstExcerptReady;

    /// <summary>
    /// Raised on the thread pool when a field's excerpt reads differently from the last one,
    /// including the first. A refresh that reads the same text raises nothing.
    /// </summary>
    public event Action<FocusedField>? ExcerptChanged;

    /// <summary>
    /// Which field a session belongs to. The window title is part of it: a chat app can keep its
    /// composer while the conversation changes, and the old conversation must not leak into the new one.
    /// </summary>
    public static string IdentityOf(FocusedField field) =>
        $"{field.ProcessId}:{field.FieldId}:{TitleKey(field.WindowTitle)}";

    /// <summary>
    /// The title without what changes while the same document or conversation stays open: the
    /// unsaved-changes mark editors add ("*notes - Notepad", "● notes", "notes • - Sublime
    /// Text") and an unread count ("(3) Discord", "Inbox (3) - Gmail", "WhatsApp (2)"). They count
    /// at the start, at the end, or just before the " - " that ends a page or document name, so
    /// "Report (2).docx" stays apart from "Report (3).docx".
    /// </summary>
    public static string TitleKey(string title) => TitleNoise().Replace(title, "").Trim();

    [GeneratedRegex(@"^\s*(?:(?:[(\[]\d+\+?[)\]]|[*●•])\s*)+|\s*(?:[(\[]\d+\+?[)\]]|[*●•])(?=\s*$|\s+[-–—|]\s)")]
    private static partial Regex TitleNoise();

    /// <summary>Starts capturing for this field unless it already is.</summary>
    public void Track(FocusedField field, bool usesEndpoint)
    {
        if (field.IsSecure)
        {
            Stop();
            return;
        }
        string identity = IdentityOf(field);
        var configuration = VisualContextConfiguration.For(usesEndpoint);
        Session? started = null;
        TaskCompletionSource? wake = null;
        lock (_gate)
        {
            if (_session is { } current && current.Identity == identity && current.Configuration == configuration)
            {
                // A request reads the field, so an edit seen here ends a slow wait at once.
                if (NoteEditLocked(current, field)) (wake, current.Wake) = (current.Wake, null);
            }
            else
            {
                StopLocked();
                started = _session = new Session(identity, configuration);
                NoteEditLocked(started, field);
            }
        }
        wake?.TrySetResult();
        if (started is not null) _ = RunAsync(started);
    }

    /// <summary>Records the field and says whether its text or caret moved since it was last seen.</summary>
    private bool NoteEditLocked(Session session, FocusedField field)
    {
        if (session.FieldText == field.Text && session.FieldCaret == field.CaretOffset) return false;
        session.FieldText = field.Text;
        session.FieldCaret = field.CaretOffset;
        session.EditedAt = _clock.GetTimestamp();
        return true;
    }

    /// <summary>Drops the excerpt and stops capturing.</summary>
    public void Stop()
    {
        lock (_gate) StopLocked();
    }

    private void StopLocked()
    {
        _session?.Cts.Cancel();
        _session?.Cts.Dispose();
        _session = null;
    }

    /// <summary>The ready excerpt when it belongs to this field and is still fresh, otherwise null.</summary>
    public string? For(FocusedField field)
    {
        lock (_gate)
        {
            if (_session is not { Excerpt: { } excerpt } session || session.Identity != IdentityOf(field)) return null;
            return _clock.GetElapsedTime(session.CapturedAt) < session.ValidFor ? excerpt : null;
        }
    }

    private async Task RunAsync(Session session)
    {
        CancellationToken ct;
        try
        {
            ct = session.Cts.Token;
        }
        catch (ObjectDisposedException)
        {
            return;
        }
        try
        {
            // Chromium and Electron hosts flap focus. Waiting for it to settle runs the capture once.
            await Task.Delay(_settle, _clock, ct).ConfigureAwait(false);
            while (!ct.IsCancellationRequested)
            {
                if (_currentField() is not { } field || IdentityOf(field) != session.Identity || field.IsSecure)
                {
                    lock (_gate)
                    {
                        if (_session == session) StopLocked();
                    }
                    return;
                }
                lock (_gate) NoteEditLocked(session, field);
                // Age counts from before the capture, so slow recognition cannot renew old pixels.
                long capturedAt = _clock.GetTimestamp();
                string? excerpt = await ExcerptAsync(field, session.Configuration, ct).ConfigureAwait(false);
                bool first = false;
                bool changed = false;
                TimeSpan wait;
                TaskCompletionSource? wake;
                lock (_gate)
                {
                    if (_session != session) return;
                    first = session.Excerpt is null && excerpt is not null && session.LastText is null;
                    changed = excerpt is not null && excerpt != session.LastText;
                    session.Excerpt = excerpt;
                    session.CapturedAt = capturedAt;
                    if (excerpt is not null) session.LastText = excerpt;
                    // Judged at the capture's own time, so a slow read cannot make its excerpt idle-aged.
                    bool idle = _clock.GetElapsedTime(session.EditedAt, capturedAt) >= _activeWindow;
                    wait = idle ? Max(_idleRefreshInterval, _refreshInterval) : _refreshInterval;
                    session.ValidFor = _lifetime + (wait - _refreshInterval);
                    // Set in the same lock as the decision, so an edit right after it still wakes the wait.
                    wake = session.Wake = idle ? new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) : null;
                }
                if (first) FirstExcerptReady?.Invoke(field);
                if (changed) ExcerptChanged?.Invoke(field);
                await WaitAsync(wait, wake, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Reading screen context failed.");
        }
    }

    /// <summary>Waits out the refresh interval, or less when <paramref name="wake"/> completes first.</summary>
    private async Task WaitAsync(TimeSpan wait, TaskCompletionSource? wake, CancellationToken ct)
    {
        if (wake is null)
        {
            await Task.Delay(wait, _clock, ct).ConfigureAwait(false);
            return;
        }
        using var delay = CancellationTokenSource.CreateLinkedTokenSource(ct);
        await Task.WhenAny(Task.Delay(wait, _clock, delay.Token), wake.Task).ConfigureAwait(false);
        // Ends the timer of a wait the wake cut short.
        delay.Cancel();
        ct.ThrowIfCancellationRequested();
    }

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

    private async Task<string?> ExcerptAsync(FocusedField field, VisualContextConfiguration configuration, CancellationToken ct)
    {
        var capture = await _reader.ReadAsync(field, configuration, ct).ConfigureAwait(false);
        if (capture is null) return null;
        string excerpt = Excerpt(capture, field, configuration);
        _logger.LogDebug("Screen context ready: {Lines} lines, {Chars} chars kept.", capture.Lines.Count, excerpt.Length);
        return excerpt.Length == 0 ? null : excerpt;
    }

    /// <summary>
    /// Cleans recognized lines into the excerpt a prompt carries. With no usable text, the window
    /// title stands in when it says something.
    /// </summary>
    public static string Excerpt(ScreenCapture capture, FocusedField field, VisualContextConfiguration configuration)
    {
        string fieldText = field.PrecedingText + " " + field.TrailingText;
        string cleaned = configuration.CapturesEntireWindow
            ? PromptContextSanitizer.Sanitize(
                VisualContextExcerptSelector.Select(capture.Lines, fieldText, capture.FocusBounds, configuration.MaxSummaryCharacters),
                configuration.MaxRecognizedCharacters)
            : PromptContextSanitizer.SanitizeOcr(
                OcrTextHygiene.Clean(capture.Lines, fieldText, maxChars: configuration.MaxRecognizedCharacters),
                configuration.MaxRecognizedCharacters);
        string bounded = PromptContextSanitizer.Sanitize(cleaned, configuration.MaxSummaryCharacters);
        if (HasMeaningfulSignal(bounded, configuration)) return bounded;

        if (capture.WindowTitle is { Length: > 0 } title)
        {
            string fromTitle = PromptContextSanitizer.Sanitize(
                PromptContextSanitizer.SanitizeOcr(title, configuration.MaxRecognizedCharacters),
                configuration.MaxSummaryCharacters);
            if (HasMeaningfulSignal(fromTitle, configuration)) return fromTitle;
        }
        return "";
    }

    /// <summary>Text that is mostly punctuation or numbers would hurt the prompt more than help it.</summary>
    private static bool HasMeaningfulSignal(string text, VisualContextConfiguration configuration)
    {
        string trimmed = ScalarSets.TrimWhitespaceAndNewlines(text);
        return GraphemeString.CountOf(trimmed) >= configuration.MinRecognizedCharacterCount
            && trimmed.EnumerateRunes().Count(System.Text.Rune.IsLetter) >= 4;
    }

    public void Dispose() => Stop();
}
