using System.Diagnostics;
using Cotabby.Core.Context;
using Cotabby.Core.Focus;
using Cotabby.Core.Input;
using Cotabby.Core.Insertion;
using Cotabby.Core.Overlay;
using Cotabby.Core.Spelling;
using Cotabby.Core.Support;
using Microsoft.Extensions.Logging;

namespace Cotabby.Core.Suggestions;

/// <summary>
/// Ties the keyboard hook, focus tracker, engine, overlay and inserter together.
/// </summary>
/// <remarks>
/// Key events arrive on the hook thread. Session state is only touched on the UI
/// context. Focus reads and generation run on the thread pool so UI Automation
/// never blocks the dispatcher. The hook thread sees the visible suggestion only
/// through <see cref="_offer"/> and <see cref="_ownership"/>.
/// </remarks>
public sealed class SuggestionCoordinator : IAsyncDisposable
{
    // The hook fires before the host processes the key, so the field is polled
    // until the typed text shows up.
    private const int HostPublishFirstPollMs = 10;
    private const int HostPublishPollMs = 30;
    private const int HostPublishCeilingMs = 400;
    private const int PostInsertSettleMs = 60;

    /// <summary>How often a visible suggestion checks that the caret is still where it was offered.</summary>
    private const int SessionWatchMs = 250;

    /// <summary>How many characters the host may still owe when the field is compared with what was typed.</summary>
    private const int PublishLagChars = 8;

    /// <summary>How much of the text before the caret is compared. The field is read through a moving window.</summary>
    private const int ComparedTailChars = 256;

    private static readonly TimeSpan GenerationWatchdog = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DoubleTapWindow = TimeSpan.FromMilliseconds(300);

    /// <summary>How long the accept key stays owned after the last chunk while the continuation generates.</summary>
    private static readonly TimeSpan PostExhaustionWindow = TimeSpan.FromMilliseconds(800);

    private readonly IKeyboardHook _hook;
    private readonly IFocusTracker _focus;
    private readonly ISuggestionEngine _engine;
    private readonly IOverlayPresenter _overlay;
    private readonly ITextInserter _inserter;
    private readonly SuggestionWorkController _work;
    private readonly ILogger<SuggestionCoordinator> _logger;
    private readonly SynchronizationContext _ui;
    private readonly Func<bool> _batterySaverOn;
    private readonly TimeProvider _clock;
    private readonly TypoCorrectionAdvisor? _typos;
    private readonly ClipboardContextSource? _clipboard;
    private readonly SuggestionDismissalMemory _dismissals = new();

    private volatile SuggestionSettings _settings = SuggestionSettings.Default;
    private volatile Offer? _offer;
    private volatile AcceptKeyOwnership? _ownership;

    private SuggestionSession? _session;
    private FocusedField? _sessionField;
    private bool _sessionAcceptedAny;

    /// <summary>The text that should sit before the caret, counting what was typed or accepted since the offer.</summary>
    private string _expectedPreceding = "";

    /// <summary>Accepted characters the host has not been sent yet.</summary>
    private int _pendingInsertChars;
    private (string RequestId, long Timestamp)? _pendingDoubleTap;
    private AcceptedTail? _lastAcceptedTail;
    private bool _queuedAccept;

    /// <summary>Text an automatic fix was last applied to. It stops a fix the host ignored from repeating.</summary>
    private volatile string? _lastAutoFixedText;
    private Task _insertChain = Task.CompletedTask;
    private bool _disposed;

    // What the field looked like at the last read, plus how many characters the
    // keys pressed since then should have added. Null means the next read is trusted as is.
    private FocusedField? _publishBaseline;
    private int _expectedDelta;
    private long _keySequence;
    private int _focusedPid;

    /// <summary>What the hook thread may accept right now.</summary>
    private sealed record Offer(KeyBinding AcceptWord, KeyBinding AcceptAll);

    private sealed record AcceptKeyOwnership(KeyBinding AcceptWord, long ExpiresAt);

    private sealed record AcceptedTail(string Text, string PrecedingText);

    public SuggestionCoordinator(
        IKeyboardHook hook,
        IFocusTracker focus,
        ISuggestionEngine engine,
        IOverlayPresenter overlay,
        ITextInserter inserter,
        SuggestionWorkController work,
        ILogger<SuggestionCoordinator> logger,
        SynchronizationContext ui,
        Func<bool>? batterySaverOn = null,
        TimeProvider? clock = null,
        TypoCorrectionAdvisor? typos = null,
        IClipboardReader? clipboard = null)
    {
        _hook = hook;
        _focus = focus;
        _engine = engine;
        _overlay = overlay;
        _inserter = inserter;
        _work = work;
        _logger = logger;
        _ui = ui;
        _batterySaverOn = batterySaverOn ?? (() => false);
        _clock = clock ?? TimeProvider.System;
        _typos = typos;
        _clipboard = clipboard is null ? null : new ClipboardContextSource(clipboard, _clock);
    }

    public SuggestionMetrics Metrics { get; } = new();

    /// <summary>True while an inline feature such as the emoji picker owns the keyboard.</summary>
    public Func<bool>? InlineCaptureActive { get; set; }

    /// <summary>
    /// True when Cotabby may act in this field. The emoji picker and macros follow the same
    /// rules as suggestions, so turning Cotabby off or excluding an app silences them too.
    /// </summary>
    public bool IsAvailableIn(FocusedField field) =>
        SuggestionAvailability.DisabledReason(_settings, _clock.GetUtcNow(), _batterySaverOn()) is null
        && SuggestionAvailability.DisabledReason(_settings, field) is null;

    /// <summary>The accept-word key in effect for an app, which is also the key inline features commit on.</summary>
    public KeyBinding AcceptWordKeyFor(string? processName)
    {
        var settings = _settings;
        return ShortcutResolver.AcceptWord(processName, settings.ShortcutOverrides, settings.AcceptWord);
    }

    public SuggestionSettings Settings
    {
        get => _settings;
        set
        {
            _settings = value;
            UpdateDebounce(value, lastLatencyMs: null);
            Post(() => CancelSession("settings changed"));
        }
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _hook.KeyEvent += OnKeyEvent;
        _focus.FocusChanged += OnFocusChanged;
        _hook.Start();
        _focus.Start();
        _logger.LogInformation("SuggestionCoordinator started.");
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _hook.KeyEvent -= OnKeyEvent;
        _focus.FocusChanged -= OnFocusChanged;
        _focus.Stop();
        _hook.Stop();
        await _work.DisposeAsync().ConfigureAwait(false);
    }

    private void OnFocusChanged(object? sender, FocusedField? field)
    {
        Post(() =>
        {
            int pid = field?.ProcessId ?? 0;
            bool sameHost = pid != 0 && (_focusedPid == 0 || pid == _focusedPid);
            _focusedPid = pid;
            if (sameHost && (_sessionField is null || _sessionField.FieldId == field!.FieldId)) return;
            _publishBaseline = null;
            _expectedDelta = 0;
            CancelSession("focus moved");
        });
    }

    private void OnKeyEvent(object? sender, KeyEventArgs args)
    {
        var ev = args.Event;
        if (!ev.IsKeyDown || args.Suppress) return;

        if (InlineCaptureActive?.Invoke() == true)
        {
            Post(() => CancelSession("inline capture active"));
            return;
        }

        if (_offer is { } offer)
        {
            bool word = offer.AcceptWord.Matches(ev);
            bool all = !word && offer.AcceptAll.Matches(ev);
            if (word || all)
            {
                // Suppressing here is the only way to eat the key before the host sees it.
                args.Suppress = true;
                _work.Cancel();
                Post(() => Accept(all, ev.IsRepeat));
                return;
            }
        }
        else if (_ownership is { } ownership
                 && ownership.AcceptWord.Matches(ev)
                 && _clock.GetTimestamp() < ownership.ExpiresAt)
        {
            args.Suppress = true;
            Post(() => _queuedAccept = _ownership is not null);
            return;
        }

        Post(() => HandleEvent(ev));
    }

    private void HandleEvent(KeyboardEvent ev)
    {
        if (ev.Kind == KeyKind.Other) return;

        _pendingDoubleTap = null;
        _ownership = null;
        _queuedAccept = false;

        if (SuggestionAvailability.DisabledReason(_settings, _clock.GetUtcNow(), _batterySaverOn()) is { } reason)
        {
            CancelSession(reason);
            return;
        }

        TrackForHostPublish(ev);

        if (_session is { } session)
        {
            if (ev.Kind == KeyKind.Escape && session.Correction is null && _sessionField is { } dismissedIn)
            {
                _dismissals.Record(
                    (ulong)dismissedIn.ProcessId, dismissedIn.PrecedingText, dismissedIn.TrailingText,
                    session.VisibleText, Now());
            }

            var result = SuggestionSessionReconciler.Apply(session, ev);
            switch (result.Outcome)
            {
                case SuggestionSessionReconciler.Outcome.Ignore:
                    return;
                case SuggestionSessionReconciler.Outcome.Advance when result.Next is { VisibleText.Length: > 0 } next:
                    _session = next;
                    _expectedPreceding += ev.Character;
                    _overlay.Update(next.VisibleText);
                    _ = ReanchorAsync(next.RequestId, HostPublishPollMs);
                    return;
                default:
                    CancelSession("typing invalidated the suggestion");
                    break;
            }
        }
        else if (SuggestionAvailability.ClearsSuggestion(ev))
        {
            _work.Cancel();
        }

        if (SuggestionAvailability.SchedulesPrediction(ev))
        {
            SchedulePrediction(ev.Kind == KeyKind.Character ? ev.Character : null);
        }
    }

    private void TrackForHostPublish(KeyboardEvent ev)
    {
        _keySequence++;
        if (ev.HasNonShiftModifier || ev.Kind is not (KeyKind.Character or KeyKind.Backspace))
        {
            // Enter, navigation and shortcuts change the text in ways a count cannot predict.
            _publishBaseline = null;
            _expectedDelta = 0;
        }
        else if (ev.Kind == KeyKind.Character)
        {
            _expectedDelta++;
        }
        else if (ev.Kind == KeyKind.Backspace)
        {
            _expectedDelta--;
        }
    }

    private void SchedulePrediction(char? typed)
    {
        var requestId = "req_" + Guid.NewGuid().ToString("N")[..8];
        var settings = _settings;
        var baseline = _publishBaseline;
        int delta = _expectedDelta;
        long sequence = _keySequence;
        _ = _work.Submit(ct => PredictAsync(requestId, settings, baseline, delta, typed, sequence, ct));
    }

    private async Task PredictAsync(
        string requestId, SuggestionSettings settings, FocusedField? baseline, int delta, char? typed,
        long sequence, CancellationToken ct)
    {
        try
        {
            var field = await WaitForHostPublishAsync(baseline, delta, typed, ct).ConfigureAwait(false);
            if (field is null) return;
            Post(() =>
            {
                if (_keySequence != sequence) return;
                _publishBaseline = field;
                _expectedDelta = 0;
            });

            if (SuggestionAvailability.DisabledReason(settings, field) is { } reason)
            {
                _logger.LogDebug("Request {ReqId} skipped: {Reason}", requestId, reason);
                return;
            }
            if (!SuggestionAvailability.HasTextToContinue(field)) return;

            string preceding = field.PrecedingText;
            if (await HandleTypoAsync(requestId, settings, field, sequence, ct).ConfigureAwait(false)) return;
            if (!settings.SuggestWithinWords && CaretWordContext.UnfinishedWord(preceding) is not null) return;
            // A completion inside a token would duplicate or splice what follows the caret.
            if (CaretTokenPosition.IsInsideToken(preceding, field.TrailingText)) return;
            if (!_engine.IsReady) return;

            var request = SuggestionRequestFactory.Build(
                field, requestId, settings, settings.ClipboardContext ? _clipboard?.For(field) : null);
            var stopwatch = Stopwatch.StartNew();
            string text = await GenerateAsync(request, ct).ConfigureAwait(false);
            int latencyMs = (int)stopwatch.ElapsedMilliseconds;
            UpdateDebounce(settings, latencyMs);
            if (text.Length == 0 || ct.IsCancellationRequested) return;

            // Generation took a while. Show the suggestion only if the text it continues is still there.
            var live = _focus.Refresh();
            if (live is null || live.ProcessId != field.ProcessId || live.PrecedingText != preceding)
            {
                _logger.LogInformation("Request {ReqId} dropped because the field changed during generation.", requestId);
                return;
            }
            if (CompletionFinalizer.VisibleText(text, live, settings, _engine.SpacingIsExact, _typos) is not { } visible)
            {
                _logger.LogInformation("Request {ReqId} dropped by the final presentation checks.", requestId);
                return;
            }

            Post(() => Present(requestId, visible, live, latencyMs, sequence, ct));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Request {ReqId} failed.", requestId);
        }
    }

    /// <summary>Paces the next request by how long the engine just took.</summary>
    private void UpdateDebounce(SuggestionSettings settings, int? lastLatencyMs) =>
        _work.Debounce = TimeSpan.FromMilliseconds(
            DebouncePolicy.Milliseconds(lastLatencyMs, settings.DebounceMs, settings.UsesEndpoint));

    /// <summary>
    /// Runs the typo gate on the word just committed. Returns true when the gate
    /// replaced the completion with a suppression, an offer or an automatic fix.
    /// </summary>
    private async Task<bool> HandleTypoAsync(
        string requestId, SuggestionSettings settings, FocusedField field, long sequence, CancellationToken ct)
    {
        if (_typos is null) return false;
        string preceding = field.PrecedingText;
        var advice = _typos.Advise(preceding, settings.SpellingDictionaries, settings.Typos);
        var decision = advice.Decision;
        switch (decision.Action)
        {
            case TypoGateAction.Proceed:
                return false;

            case TypoGateAction.ApplyCorrection when advice.Replacement is not null:
                if (_lastAutoFixedText == preceding) return true;
                _lastAutoFixedText = preceding;
                ct.ThrowIfCancellationRequested();
                await ApplyCorrectionAsync(
                    new TypoCorrection(decision.Word!, decision.CorrectedWord!), requiresTrailingSpace: true)
                    .ConfigureAwait(false);
                return true;

            case TypoGateAction.OfferCorrection:
                var correction = new TypoCorrection(decision.Word!, decision.CorrectedWord!);
                Post(() => Present(requestId, decision.CorrectedWord!, field, latencyMs: 0, sequence, ct, correction));
                return true;

            default:
                return true;
        }
    }

    /// <summary>Predicts again after an edit whose size the key count cannot describe.</summary>
    private void RestartPrediction()
    {
        _publishBaseline = null;
        _expectedDelta = 0;
        SchedulePrediction(typed: null);
    }

    private async Task<FocusedField?> WaitForHostPublishAsync(
        FocusedField? baseline, int delta, char? typed, CancellationToken ct)
    {
        int waited = 0;
        int interval = HostPublishFirstPollMs;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var field = _focus.Refresh();
            if (field is null) return null;
            if (waited >= HostPublishCeilingMs || HasPublished(field, baseline, delta, typed, waited)) return field;
            await Task.Delay(interval, ct).ConfigureAwait(false);
            waited += interval;
            interval = HostPublishPollMs;
        }
    }

    private static bool HasPublished(FocusedField field, FocusedField? baseline, int delta, char? typed, int waited)
    {
        var preceding = field.PrecedingText;
        if (baseline is null || baseline.ProcessId != field.ProcessId)
        {
            // With nothing to count against, the typed character itself is the evidence. A dead key
            // can make the host insert an accented form of it, which then waits out the ceiling.
            return typed is { } c ? preceding.EndsWith(c) : waited > 0;
        }

        int expected = baseline.PrecedingText.Length + delta;
        return delta switch
        {
            > 0 => preceding.Length >= expected,
            < 0 => preceding.Length <= expected,
            _ => true,
        };
    }

    private async Task<string> GenerateAsync(SuggestionRequest request, CancellationToken ct)
    {
        using var watchdog = new CancellationTokenSource(GenerationWatchdog);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, watchdog.Token);
        try
        {
            return await _engine.GenerateAsync(request, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (!ct.IsCancellationRequested) _logger.LogWarning("Request {ReqId} hit the watchdog.", request.RequestId);
            return string.Empty;
        }
    }

    private void Present(
        string requestId, string text, FocusedField field, int latencyMs, long sequence, CancellationToken ct,
        TypoCorrection? correction = null)
    {
        // A key handled after the work finished can no longer cancel it, so the sequence is checked too.
        if (ct.IsCancellationRequested || _session is not null || _keySequence != sequence) return;

        var preceding = field.PrecedingText;
        if (correction is null
            && _dismissals.Suppresses((ulong)field.ProcessId, preceding, field.TrailingText, text, Now()))
        {
            _logger.LogInformation("Request {ReqId} dropped because the user dismissed it here.", requestId);
            return;
        }
        if (_lastAcceptedTail is { } tail
            && AcceptanceChunker.IsStaleAcceptanceEcho(text, tail.Text, preceding, tail.PrecedingText))
        {
            _logger.LogInformation("Request {ReqId} dropped as an echo of the last accepted chunk.", requestId);
            return;
        }
        _lastAcceptedTail = null;

        _session = new SuggestionSession
        {
            RequestId = requestId,
            VisibleText = text,
            Correction = correction,
        };
        _sessionField = field;
        _sessionAcceptedAny = false;
        _expectedPreceding = preceding;
        PublishOffer();
        _overlay.Show(text, field.CaretRect, StyleFor(field, correction));
        if (correction is null) Metrics.RecordShown(latencyMs);
        _logger.LogInformation("Suggestion shown req={ReqId} latency={Ms}ms chars={Chars}", requestId, latencyMs, text.Length);
        _logger.LogDebug("Suggestion text req={ReqId} \"{Text}\"", requestId, LogText.Escape(text));

        _ = WatchSessionAsync(requestId);

        // A queued press only counts while the key is still owned. Later it would accept unseen text.
        bool acceptQueued = _queuedAccept && _ownership is { } owned && _clock.GetTimestamp() < owned.ExpiresAt;
        _ownership = null;
        _queuedAccept = false;
        if (acceptQueued && correction is null) Accept(all: false, isRepeat: true);
    }

    /// <summary>
    /// Drops the suggestion when the caret leaves the place it was offered at. A click elsewhere in
    /// the same app raises no key and often no focus event, so the field is checked while it shows.
    /// </summary>
    private async Task WatchSessionAsync(string requestId)
    {
        while (!_disposed)
        {
            await Task.Delay(SessionWatchMs).ConfigureAwait(false);
            var live = _focus.Refresh();
            var watching = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Post(() =>
            {
                bool current = _session?.RequestId == requestId;
                if (current && !IsStillAtOffer(live))
                {
                    CancelSession("the caret left the suggestion");
                    current = false;
                }
                watching.SetResult(current);
            });
            if (!await watching.Task.ConfigureAwait(false)) return;
        }
    }

    private bool IsStillAtOffer(FocusedField? live) =>
        live is not null && _sessionField is { } offeredIn
        && live.ProcessId == offeredIn.ProcessId && live.FieldId == offeredIn.FieldId
        && FollowsExpected(_expectedPreceding, live.PrecedingText, PublishLagChars + _pendingInsertChars);

    /// <summary>
    /// True when <paramref name="live"/> is the expected text, or the expected text less a few
    /// trailing characters the host has not published yet.
    /// </summary>
    private static bool FollowsExpected(string expected, string live, int allowedLag)
    {
        // A short text is the whole of what precedes the caret, so it has to match from the start.
        if (live.Length < ComparedTailChars)
        {
            return expected.Length - live.Length <= allowedLag && expected.StartsWith(live, StringComparison.Ordinal);
        }
        int at = expected.LastIndexOf(live[^ComparedTailChars..], StringComparison.Ordinal);
        return at >= 0 && expected.Length - (at + ComparedTailChars) <= allowedLag;
    }

    private void Accept(bool all, bool isRepeat)
    {
        if (_session is not { } session || _sessionField is not { } field)
        {
            // The press raced the accept that exhausted the suggestion, so it belongs to the continuation.
            _queuedAccept = !all && _ownership is not null;
            return;
        }

        var settings = _settings;
        if (session.Correction is { } correction)
        {
            ClearSession();
            _insertChain = _insertChain
                .ContinueWith(
                    _ => ApplyCorrectionAsync(correction, requiresTrailingSpace: false), TaskScheduler.Default)
                .Unwrap();
            return;
        }

        bool doubleTapApplies = settings.DoubleTapAcceptsAll
            && !isRepeat
            && !ShortcutResolver.OverridesAcceptAll(field.ProcessName, settings.ShortcutOverrides);
        long now = _clock.GetTimestamp();
        if (!all && doubleTapApplies
            && _pendingDoubleTap is { } pending
            && pending.RequestId == session.RequestId
            && _clock.GetElapsedTime(pending.Timestamp, now) <= DoubleTapWindow)
        {
            all = true;
        }
        _pendingDoubleTap = null;

        string visible = session.VisibleText;
        string chunk = all
            ? visible
            : settings.Granularity == AcceptanceGranularity.Phrase
                ? AcceptanceChunker.NextPhrase(visible, settings.AcceptPunctuationWithWord)
                : AcceptanceChunker.NextChunk(visible, settings.AcceptPunctuationWithWord);
        if (chunk.Length == 0) chunk = visible;
        if (settings.AddSpaceAfterAccept) chunk = AcceptanceChunker.ConsumingTrailingSpace(chunk, visible);

        string remaining = visible[chunk.Length..];
        bool exhausted = string.IsNullOrWhiteSpace(remaining);
        bool firstChunk = !_sessionAcceptedAny;

        string expected = _expectedPreceding;
        string insertion = AcceptanceChunker.InsertionChunk(chunk, expected);
        if (exhausted && settings.AddSpaceAfterAccept) insertion = AcceptanceChunker.AppendingTrailingSpace(insertion);
        var accepted = new AcceptedChunk(session.RequestId, field, expected, chunk, insertion, exhausted, firstChunk);
        _expectedPreceding = expected + insertion;
        _pendingInsertChars += insertion.Length;

        if (exhausted)
        {
            ClearSession();
            // Keep owning the accept key so a fast follow-up press is queued for the
            // continuation instead of leaking into the host.
            _ownership = new AcceptKeyOwnership(
                ShortcutResolver.AcceptWord(field.ProcessName, settings.ShortcutOverrides, settings.AcceptWord),
                now + (long)(PostExhaustionWindow.TotalSeconds * _clock.TimestampFrequency));
        }
        else
        {
            _session = session with { VisibleText = remaining };
            _sessionAcceptedAny = true;
            _overlay.Update(remaining);
            if (!all && doubleTapApplies) _pendingDoubleTap = (session.RequestId, now);
        }

        _insertChain = _insertChain
            .ContinueWith(_ => InsertAcceptedAsync(accepted), TaskScheduler.Default)
            .Unwrap();
    }

    /// <summary>One accept, decided on the UI context and carried out on the thread pool.</summary>
    private sealed record AcceptedChunk(
        string RequestId, FocusedField OfferedIn, string ExpectedPreceding, string Chunk, string Insertion,
        bool Exhausted, bool FirstChunk);

    private async Task InsertAcceptedAsync(AcceptedChunk accepted)
    {
        try
        {
            // The text goes where the caret is, so it is only sent if the caret is still where the
            // suggestion was shown.
            var field = _focus.Refresh();
            if (field is null
                || field.ProcessId != accepted.OfferedIn.ProcessId || field.FieldId != accepted.OfferedIn.FieldId
                || !FollowsExpected(accepted.ExpectedPreceding, field.PrecedingText, PublishLagChars))
            {
                Post(() => CancelSession("the caret left the suggestion before the accept"));
                return;
            }

            if (accepted.Insertion.Length > 0
                && !await _inserter.InsertAsync(field, accepted.Insertion, CancellationToken.None).ConfigureAwait(false))
            {
                Post(() => CancelSession("insertion failed"));
                return;
            }
            Metrics.RecordAccepted(accepted.Chunk, accepted.FirstChunk);
            _logger.LogInformation(
                "Accepted req={ReqId} chars={Chars} exhausted={Exhausted}",
                accepted.RequestId, accepted.Insertion.Length, accepted.Exhausted);
            _logger.LogDebug(
                "Accepted text req={ReqId} \"{Inserted}\"", accepted.RequestId, LogText.Escape(accepted.Insertion));

            if (accepted.Exhausted)
            {
                Post(() =>
                {
                    _lastAcceptedTail = new AcceptedTail(accepted.Chunk, field.PrecedingText);
                    _publishBaseline = field;
                    _expectedDelta = accepted.Insertion.Length;
                    SchedulePrediction(typed: null);
                });
                return;
            }

            await ReanchorAsync(accepted.RequestId, PostInsertSettleMs).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Accept failed for {ReqId}.", accepted.RequestId);
            Post(() => CancelSession("accept failed"));
        }
        finally
        {
            Post(() => _pendingInsertChars -= accepted.Insertion.Length);
        }
    }

    /// <summary>
    /// Swaps the typo for its fix. The edit is planned against the live text, and once it starts
    /// it is not cancelled, because a cancelled edit would leave the word deleted.
    /// </summary>
    private async Task ApplyCorrectionAsync(TypoCorrection correction, bool requiresTrailingSpace)
    {
        try
        {
            if (_focus.Refresh() is not { } field) return;
            var plan = TypoCorrectionReplacementPlanner.Plan(
                field.PrecedingText, correction.Word, correction.CorrectedWord, requiresTrailingSpace);
            if (plan is not { } fix) return;
            await _inserter.ReplaceAsync(field, fix.DeletingUtf16Count, fix.ReplacementText, CancellationToken.None)
                .ConfigureAwait(false);
            Post(RestartPrediction);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Applying a correction failed.");
        }
    }

    /// <summary>Moves the ghost to the caret once the host has published an insert or a typed key.</summary>
    private async Task ReanchorAsync(string requestId, int settleMs)
    {
        await Task.Delay(settleMs).ConfigureAwait(false);
        var after = _focus.Refresh();
        if (after is null || after.CaretRect.IsEmpty) return;
        Post(() =>
        {
            if (_session is not { } live || live.RequestId != requestId) return;
            _sessionField = after;
            _overlay.Show(live.VisibleText, after.CaretRect, StyleFor(after, live.Correction));
        });
    }

    private OverlayStyle StyleFor(FocusedField field, TypoCorrection? correction) =>
        OverlayStylePolicy.StyleFor(_settings.DisplayMode, field, correction is not null);

    private void PublishOffer()
    {
        var settings = _settings;
        var process = _sessionField?.ProcessName;
        _offer = new Offer(
            ShortcutResolver.AcceptWord(process, settings.ShortcutOverrides, settings.AcceptWord),
            ShortcutResolver.AcceptAll(process, settings.ShortcutOverrides, settings.AcceptAll));
    }

    private void ClearSession()
    {
        _session = null;
        _sessionField = null;
        _offer = null;
        _overlay.Hide();
    }

    private void CancelSession(string reason)
    {
        if (_session is not null) _logger.LogInformation("Suggestion cleared ({Reason}).", reason);
        _pendingDoubleTap = null;
        _ownership = null;
        _queuedAccept = false;
        ClearSession();
        _work.Cancel();
    }

    private double Now() => _clock.GetTimestamp() / (double)_clock.TimestampFrequency;

    private void Post(Action action)
    {
        if (SynchronizationContext.Current == _ui) action();
        else _ui.Post(_ => action(), null);
    }
}
