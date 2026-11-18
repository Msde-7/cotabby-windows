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
public sealed partial class ScreenContextSource : IDisposable
{
    private readonly IScreenTextReader _reader;
    private readonly Func<FocusedField?> _currentField;
    private readonly TimeProvider _clock;
    private readonly ILogger _logger;
    private readonly TimeSpan _settle;
    private readonly TimeSpan _refreshInterval;
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

        /// <summary>The last text read, kept through a failed capture so reading it again is no change.</summary>
        public string? LastText;
    }

    public ScreenContextSource(
        IScreenTextReader reader, Func<FocusedField?> currentField, ILogger logger, TimeProvider? clock = null,
        TimeSpan? settle = null, TimeSpan? refreshInterval = null, TimeSpan? lifetime = null)
    {
        _reader = reader;
        _currentField = currentField;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
        _settle = settle ?? TimeSpan.FromMilliseconds(250);
        _refreshInterval = refreshInterval ?? TimeSpan.FromSeconds(3);
        _lifetime = lifetime ?? TimeSpan.FromSeconds(6);
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
        Session session;
        lock (_gate)
        {
            if (_session is { } current && current.Identity == identity && current.Configuration == configuration) return;
            StopLocked();
            session = _session = new Session(identity, configuration);
        }
        _ = RunAsync(session);
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
            return _clock.GetElapsedTime(session.CapturedAt) < _lifetime ? excerpt : null;
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
                // Age counts from before the capture, so slow recognition cannot renew old pixels.
                long capturedAt = _clock.GetTimestamp();
                string? excerpt = await ExcerptAsync(field, session.Configuration, ct).ConfigureAwait(false);
                bool first = false;
                bool changed = false;
                lock (_gate)
                {
                    if (_session != session) return;
                    first = session.Excerpt is null && excerpt is not null && session.LastText is null;
                    changed = excerpt is not null && excerpt != session.LastText;
                    session.Excerpt = excerpt;
                    session.CapturedAt = capturedAt;
                    if (excerpt is not null) session.LastText = excerpt;
                }
                if (first) FirstExcerptReady?.Invoke(field);
                if (changed) ExcerptChanged?.Invoke(field);
                await Task.Delay(_refreshInterval, _clock, ct).ConfigureAwait(false);
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
