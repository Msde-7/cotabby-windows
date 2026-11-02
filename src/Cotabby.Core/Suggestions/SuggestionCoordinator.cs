using System.Diagnostics;
using Cotabby.Core.Context;
using Cotabby.Core.Focus;
using Cotabby.Core.History;
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

    /// <summary>A suggestion this close to typed through has its continuation generated ahead.</summary>
    private const int ContinuationPrefetchChars = 9;

    /// <summary>How often delivery checks whether the host shows the keys typed during generation.</summary>
    private const int TypedAheadPollMs = 20;

    private static readonly TimeSpan GenerationWatchdog = TimeSpan.FromSeconds(10);

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
    private bool? _batterySaverWasOn;
    private volatile Offer? _offer;
    private volatile AcceptKeyOwnership? _ownership;

    private SuggestionSession? _session;
    private FocusedField? _sessionField;
    private bool _sessionAcceptedAny;
    private bool _sessionPrefetched;
    private CancellationTokenSource? _prefetch;
    private PrefetchedContinuation? _prefetched;
    private volatile TypingPrediction? _prediction;

    /// <summary>The text that should sit before the caret, counting what was typed or accepted since the offer.</summary>
    private string _expectedPreceding = "";

    /// <summary>Accepted characters the host has not been sent yet.</summary>
    private int _pendingInsertChars;
    private readonly DoubleTapAcceptanceState _doubleTap = new();
    private readonly TypingCadence _cadence = new();
    private volatile string _focusIdentity = "";
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

    /// <summary>A generation for the text a suggestion leaves once typed through, ready before it is.</summary>
    private sealed record PrefetchedContinuation(string FocusIdentity, string PrecedingText, string Text);

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

    /// <summary>The user's typing history, read for prompt examples. Null runs without it.</summary>
    public ISuggestionHistoryProvider? History { get; set; }

    /// <summary>Records what the user types. It sees each fresh read of the focused field.</summary>
    public ITypingHistoryObserver? HistoryRecorder { get; set; }

    /// <summary>
    /// True when Cotabby may act in this field. The emoji picker and macros follow the same
    /// rules as suggestions, so turning Cotabby off or excluding an app silences them too.
    /// </summary>
    public bool IsAvailableIn(FocusedField field) =>
        PausedReason() is null && SuggestionAvailability.DisabledReason(_settings, field) is null;

    /// <summary>The accept-word key in effect for an app, which is also the key inline features commit on.</summary>
    public KeyBinding AcceptWordKeyFor(string? processName)
    {
        var settings = _settings;
        return ShortcutResolver.AcceptWord(processName, settings.ShortcutOverrides, settings.AcceptWord);
    }

    /// <summary>Why suggestions are off everywhere right now, or null.</summary>
    private string? PausedReason() =>
        SuggestionAvailability.DisabledReason(_settings, _clock.GetUtcNow(), _batterySaverOn());

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

    /// <summary>
    /// Re-checks availability when battery saver turns on or off, instead of waiting for the next
    /// key. A visible suggestion goes away when suggestions pause, and a new one is asked for
    /// when battery saver had paused them and they resume.
    /// </summary>
    public void BatterySaverChanged() => Post(() =>
    {
        bool on = _batterySaverOn();
        bool wasOn = _batterySaverWasOn ?? !on;
        _batterySaverWasOn = on;
        if (SuggestionAvailability.DisabledReason(_settings, _clock.GetUtcNow(), on) is { } reason)
        {
            CancelSession(reason);
            return;
        }
        if (_session is not null || !wasOn || on || !_settings.PauseOnBatterySaver) return;
        SetPublishBaseline(null);
        SchedulePrediction(typed: null);
    });

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
        _prefetch?.Cancel();
        _focus.Stop();
        _hook.Stop();
        await _work.DisposeAsync().ConfigureAwait(false);
    }

    private void OnFocusChanged(object? sender, FocusedField? field)
    {
        Post(() =>
        {
            RecordHistory(field);
            int pid = field?.ProcessId ?? 0;
            _focusIdentity = field is null ? "" : FocusIdentity(field);
            bool sameHost = pid != 0 && (_focusedPid == 0 || pid == _focusedPid);
            _focusedPid = pid;
            if (sameHost && (_sessionField is null || _sessionField.FieldId == field!.FieldId)) return;
            SetPublishBaseline(null);
            CancelSession("focus moved");
            CancelPrefetch();
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
        if (ev.Kind == KeyKind.Other)
        {
            // Any key between two accept presses breaks the pair. Modifiers alone do not.
            if (!KeyClassifier.IsModifier(ev.VirtualKey)) _doubleTap.Reset();
            return;
        }

        ResetAcceptKeyState();

        if (PausedReason() is { } reason)
        {
            CancelSession(reason);
            return;
        }

        TrackForHostPublish(ev);
        _cadence.Record(_focusIdentity, ev.Kind == KeyKind.Character ? ev.Character.ToString() : "", Now());

        if (_session is { } session)
        {
            if (ev.Kind == KeyKind.Escape && _sessionField is { } dismissedIn)
            {
                _dismissals.Record(
                    DismissalScope(dismissedIn), dismissedIn.PrecedingText, dismissedIn.TrailingText,
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
                    PrefetchContinuation(next);
                    return;
                case SuggestionSessionReconciler.Outcome.Advance:
                    // Typed through to the end, so a prefetched continuation may be about to answer.
                    ClearSession();
                    _work.Cancel();
                    break;
                default:
                    CancelSession("typing invalidated the suggestion");
                    CancelPrefetch();
                    break;
            }
        }
        else if (KeepsPrediction(ev))
        {
            return;
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
            SetPublishBaseline(null);
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

    /// <summary>
    /// A typed letter the running generation agrees with keeps it going instead of starting over,
    /// so the suggestion is ready when the user pauses.
    /// </summary>
    private bool KeepsPrediction(KeyboardEvent ev) =>
        ev.Kind == KeyKind.Character && !ev.HasNonShiftModifier
        && _prediction is { } prediction && prediction.Append(ev.Character.ToString(), Now(), _keySequence);

    /// <summary>Gives up on a prediction typing moved past and asks again for the live text.</summary>
    private void RestartPrediction(TypingPrediction prediction, string reason) => Post(() =>
    {
        if (_prediction != prediction) return;
        _logger.LogDebug("Prediction restarted: {Reason}.", reason);
        SchedulePrediction(typed: null);
    });

    /// <param name="requiresTextChange">Wait until the text differs from the baseline, as after a correction.</param>
    private void SchedulePrediction(char? typed, bool requiresTextChange = false)
    {
        _prediction = null;
        var requestId = "req_" + Guid.NewGuid().ToString("N")[..8];
        var settings = _settings;
        var baseline = _publishBaseline;
        int delta = _expectedDelta;
        long sequence = _keySequence;
        _ = _work.Submit(ct => PredictAsync(requestId, settings, baseline, delta, typed, requiresTextChange, sequence, ct));
    }

    private async Task PredictAsync(
        string requestId, SuggestionSettings settings, FocusedField? baseline, int delta, char? typed,
        bool requiresTextChange, long sequence, CancellationToken ct)
    {
        TypingPrediction? prediction = null;
        bool presented = false;
        try
        {
            var field = await WaitForHostPublishAsync(baseline, delta, typed, requiresTextChange, ct)
                .ConfigureAwait(false);
            RecordHistory(field);
            if (field is null) return;
            Post(() =>
            {
                if (_keySequence == sequence) SetPublishBaseline(field);
            });

            if (SuggestionAvailability.DisabledReason(settings, field) is { } reason)
            {
                _logger.LogDebug("Request {ReqId} skipped: {Reason}", requestId, reason);
                return;
            }
            if (field.HostIsPredicting)
            {
                _logger.LogDebug("Request {ReqId} skipped: the app shows its own suggestion.", requestId);
                return;
            }
            if (!SuggestionAvailability.HasTextToContinue(field)) return;

            string preceding = field.PrecedingText;
            if (await HandleTypoAsync(requestId, settings, field, sequence, ct).ConfigureAwait(false)) return;
            // A completion inside a token would duplicate or splice what follows the caret.
            if (!SuggestionRequestFactory.ShouldGenerate(preceding, field.TrailingText, settings.SuggestWithinWords)) return;
            if (!_engine.IsReady) return;

            var stopwatch = Stopwatch.StartNew();
            string text;
            if (TakePrefetched(field) is { } prefetched)
            {
                text = prefetched;
                _logger.LogDebug("Request {ReqId} answered by the prefetched continuation.", requestId);
            }
            else
            {
                // An endpoint is billed per request, so it gets no lookahead.
                if (settings.PredictAheadWhileTyping && !settings.UsesEndpoint)
                {
                    prediction = _prediction = new TypingPrediction(field, sequence, ct);
                }
                text = await GenerateAsync(BuildRequest(field, requestId, settings), ct, prediction is null ? null : partial =>
                {
                    if (!prediction.Receive(partial, final: false)) RestartPrediction(prediction, "the model left what was typed");
                }).ConfigureAwait(false);
                UpdateDebounce(settings, (int)stopwatch.ElapsedMilliseconds);
            }
            int latencyMs = (int)stopwatch.ElapsedMilliseconds;
            ct.ThrowIfCancellationRequested();
            if (prediction is not null && !prediction.Receive(text, final: true))
            {
                RestartPrediction(prediction, "the prediction does not cover what was typed");
                return;
            }
            if (text.Length == 0 && CaretWordContext.UnfinishedWord(preceding) is null) return;

            if (await AwaitDeliveryAsync(requestId, field, prediction, sequence, ct).ConfigureAwait(false) is not { } delivery)
            {
                return;
            }
            var (live, typedAhead, shownAt) = delivery;
            if (typedAhead.Length > 0)
            {
                text = text[typedAhead.Length..];
                if (string.IsNullOrWhiteSpace(text))
                {
                    RestartPrediction(prediction!, "typing used up the prediction");
                    return;
                }
                if (!SuggestionRequestFactory.ShouldGenerate(live.PrecedingText, live.TrailingText, settings.SuggestWithinWords)) return;
                _logger.LogDebug("Request {ReqId} kept through {Count} typed characters.", requestId, typedAhead.Length);
            }
            // An unusable result mid-word may still offer a local word ending.
            if (CompletionFinalizer.Finalize(text, live, settings, _engine.SpacingIsExact, _typos) is not { } visible)
            {
                _logger.LogInformation("Request {ReqId} dropped by the final presentation checks.", requestId);
                return;
            }

            // Suggestions may have been paused, or battery saver turned on, while this generated.
            if (PausedReason() is { } paused)
            {
                _logger.LogInformation("Request {ReqId} dropped: {Reason}", requestId, paused);
                return;
            }

            presented = true;
            Post(() =>
            {
                Present(requestId, visible, live, latencyMs, shownAt, ct);
                // A key kept after the field was read changed the text this answer was checked against.
                if (_keySequence != shownAt && _session is null && !ct.IsCancellationRequested) SchedulePrediction(typed: null);
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Request {ReqId} failed.", requestId);
        }
        finally
        {
            // A finished request keeps no keys. Keys it kept for an answer that was then dropped
            // still need a request of their own.
            if (prediction?.Close() is { Length: > 0 } && !presented && !ct.IsCancellationRequested)
            {
                RestartPrediction(prediction, "the answer was dropped after keys were kept");
            }
            else if (prediction is not null)
            {
                Post(() =>
                {
                    if (_prediction == prediction) _prediction = null;
                });
            }
        }
    }

    private static string FocusIdentity(FocusedField field) => $"{field.ProcessId}:{field.FieldId}";

    /// <summary>Dismissals hold within one field, so the same words stay offered in another. The memory lives in this process only.</summary>
    private static ulong DismissalScope(FocusedField field) =>
        (uint)HashCode.Combine(field.ProcessId, field.FieldId, field.HelpText);

    private static bool SameField(FocusedField a, FocusedField b) => a.ProcessId == b.ProcessId && a.FieldId == b.FieldId;

    private void SetPublishBaseline(FocusedField? field, int delta = 0)
    {
        _publishBaseline = field;
        _expectedDelta = delta;
    }

    private SuggestionRequest BuildRequest(FocusedField field, string requestId, SuggestionSettings settings) =>
        SuggestionRequestFactory.Build(
            field, requestId, settings, settings.ClipboardContext ? _clipboard?.For(field) : null,
            History?.HistoryExamples(field, settings.UsesEndpoint));

    /// <summary>
    /// Generates what follows a suggestion that is nearly typed through, so the suggestion after it is
    /// ready when the user reaches its end. It runs beside the current work, which it must not cancel,
    /// and only fills <see cref="_prefetched"/>, which nothing shows until the live text matches it.
    /// </summary>
    private void PrefetchContinuation(SuggestionSession session)
    {
        if (_sessionPrefetched || session.Correction is not null || _sessionField is not { } offeredIn
            || session.PredictedText.Length > ContinuationPrefetchChars || session.IsExhausted || !_engine.IsReady)
        {
            return;
        }
        var settings = _settings;
        // The text once the suggestion is typed through, from the session rather than the field,
        // which may not have published the last keys yet.
        string preceding = _expectedPreceding + session.PredictedText;
        if (!SuggestionRequestFactory.ShouldGenerate(preceding, offeredIn.TrailingText, settings.SuggestWithinWords)) return;
        _sessionPrefetched = true;

        var field = offeredIn with { Text = preceding + offeredIn.TrailingText, CaretOffset = preceding.Length };
        string identity = FocusIdentity(field);
        CancelPrefetch();
        var cts = _prefetch = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            try
            {
                var request = BuildRequest(field, "pre_" + Guid.NewGuid().ToString("N")[..8], settings);
                string text = await GenerateAsync(request, cts.Token).ConfigureAwait(false);
                if (text.Length == 0 || cts.IsCancellationRequested) return;
                Post(() => _prefetched = new PrefetchedContinuation(identity, preceding, text));
                _logger.LogDebug("Prefetched the continuation of {ReqId}.", session.RequestId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Prefetching the continuation of {ReqId} failed.", session.RequestId);
            }
        });
    }

    /// <summary>The prefetched continuation when it was generated for this very text, used once.</summary>
    private string? TakePrefetched(FocusedField field)
    {
        var prefetched = Interlocked.Exchange(ref _prefetched, null);
        return prefetched is not null
            && prefetched.FocusIdentity == FocusIdentity(field)
            && SuggestionSessionReconciler.SpaceNormalized(prefetched.PrecedingText)
                == SuggestionSessionReconciler.SpaceNormalized(field.PrecedingText)
            ? prefetched.Text
            : null;
    }

    private void CancelPrefetch()
    {
        _prefetch?.Cancel();
        _prefetch = null;
        _prefetched = null;
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
                Post(() => Present(
                    requestId, new FinalCompletion(decision.CorrectedWord!), field, latencyMs: 0, sequence, ct, correction));
                return true;

            default:
                return true;
        }
    }

    private async Task<FocusedField?> WaitForHostPublishAsync(
        FocusedField? baseline, int delta, char? typed, bool requiresTextChange, CancellationToken ct)
    {
        int waited = 0;
        int interval = HostPublishFirstPollMs;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var field = _focus.Refresh();
            if (field is null) return null;
            if (waited >= HostPublishCeilingMs || HasPublished(field, baseline, delta, typed, requiresTextChange, waited))
            {
                return field;
            }
            await Task.Delay(interval, ct).ConfigureAwait(false);
            waited += interval;
            interval = HostPublishPollMs;
        }
    }

    private static bool HasPublished(
        FocusedField field, FocusedField? baseline, int delta, char? typed, bool requiresTextChange, int waited)
    {
        var preceding = field.PrecedingText;
        if (requiresTextChange && baseline is not null)
        {
            return SuggestionSessionReconciler.SpaceNormalized(preceding)
                != SuggestionSessionReconciler.SpaceNormalized(baseline.PrecedingText);
        }
        if (baseline is null || baseline.ProcessId != field.ProcessId)
        {
            // With nothing to count against, the typed character itself is the evidence. A dead key
            // can make the host insert an accented form of it, which then waits out the ceiling.
            return typed is { } c
                ? preceding.EndsWith(c) || (c == ' ' && preceding.EndsWith('\u00A0'))
                : waited > 0;
        }

        int expected = baseline.PrecedingText.Length + delta;
        return delta switch
        {
            > 0 => preceding.Length >= expected,
            < 0 => preceding.Length <= expected,
            _ => true,
        };
    }

    /// <summary>
    /// Waits for a pause in typing and for the host to show what was typed during generation, then
    /// reads the field the answer is shown against. Null drops the answer, after asking again when
    /// the typing it kept could not be confirmed.
    /// </summary>
    private async Task<(FocusedField Live, string TypedAhead, long Sequence)?> AwaitDeliveryAsync(
        string requestId, FocusedField field, TypingPrediction? prediction, long requestSequence, CancellationToken ct)
    {
        string? pausedFor = null;
        while (true)
        {
            var (typed, sequence) = prediction?.Progress ?? ("", requestSequence);
            string expected = field.PrecedingText + typed;
            // Mid-word, a result waits for a pause a little longer than the recent gap between
            // letters, so it does not flash up between two keys. Cancellation still owns the wait.
            if (pausedFor != expected)
            {
                pausedFor = expected;
                double pause = _cadence.RemainingDelay(FocusIdentity(field), expected, Now());
                if (pause > 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(pause), ct).ConfigureAwait(false);
                    continue;
                }
            }

            // Generation took a while. Show the suggestion only if the text it continues is still there.
            var live = _focus.Refresh(readHostText: true);
            ct.ThrowIfCancellationRequested();
            if (live is not null && live.ProcessId == field.ProcessId && !live.HostIsPredicting
                && SuggestionSessionReconciler.SpaceNormalized(live.PrecedingText)
                    == SuggestionSessionReconciler.SpaceNormalized(expected))
            {
                if (prediction is null || prediction.Progress == (typed, sequence)) return (live, typed, sequence);
                continue;
            }
            if (prediction is null || typed.Length == 0 || live?.HostIsPredicting == true)
            {
                _logger.LogInformation("Request {ReqId} dropped because the field changed during generation.", requestId);
                return null;
            }
            if (live is null || !prediction.Accepts(live) || prediction.Expiration(live) <= Now())
            {
                RestartPrediction(prediction, "the field did not show what was typed");
                return null;
            }
            await Task.Delay(TypedAheadPollMs, ct).ConfigureAwait(false);
        }
    }

    private async Task<string> GenerateAsync(SuggestionRequest request, CancellationToken ct, Action<string>? onPartial = null)
    {
        using var watchdog = new CancellationTokenSource(GenerationWatchdog);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, watchdog.Token);
        try
        {
            return await _engine.GenerateAsync(request, linked.Token, onPartial).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (!ct.IsCancellationRequested) _logger.LogWarning("Request {ReqId} hit the watchdog.", request.RequestId);
            return string.Empty;
        }
    }

    private void Present(
        string requestId, FinalCompletion completion, FocusedField field, int latencyMs, long sequence,
        CancellationToken ct, TypoCorrection? correction = null)
    {
        // A key handled after the work finished can no longer cancel it, so the sequence is checked too.
        if (ct.IsCancellationRequested || _session is not null || _keySequence != sequence) return;

        var session = new SuggestionSession
        {
            RequestId = requestId,
            PredictedText = completion.Predicted,
            FirstOfferLength = completion.FirstOfferLength,
            ShowFollowingWords = _settings.ShowFollowingWords,
            AcceptPunctuationWithWord = _settings.AcceptPunctuationWithWord,
            Correction = correction,
        };
        string text = session.VisibleText;
        var preceding = field.PrecedingText;
        if (_dismissals.Suppresses(DismissalScope(field), preceding, field.TrailingText, text, Now()))
        {
            _logger.LogInformation("Request {ReqId} dropped because the user dismissed it here.", requestId);
            return;
        }
        if (_lastAcceptedTail is { } tail
            && AcceptanceChunker.IsStaleAcceptanceEcho(completion.Predicted, tail.Text, preceding, tail.PrecedingText))
        {
            _logger.LogInformation("Request {ReqId} dropped as an echo of the last accepted chunk.", requestId);
            return;
        }
        _lastAcceptedTail = null;

        _session = session;
        _sessionField = field;
        _sessionAcceptedAny = false;
        _sessionPrefetched = false;
        _expectedPreceding = preceding;
        PublishOffer();
        _overlay.Show(text, field.CaretRect, StyleFor(field, correction), field);
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
                if (current && live?.HostIsPredicting == true)
                {
                    CancelSession("the app shows its own suggestion");
                    current = false;
                }
                else if (current && !IsStillAtOffer(live))
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
        live is not null && _sessionField is { } offeredIn && SameField(live, offeredIn)
        && SuggestionSessionReconciler.FollowsExpected(
            _expectedPreceding, live.PrecedingText, PublishLagChars + _pendingInsertChars);

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
            // Keep the key for a moment so a quick second press accepts the next suggestion
            // instead of reaching the host as a Tab.
            OwnAcceptKeyBriefly(field, settings);
            string offeredFor = _expectedPreceding;
            Chain(() => ApplyCorrectionAsync(correction, requiresTrailingSpace: false, (field, offeredFor)));
            return;
        }

        bool doubleTapApplies = settings.DoubleTapAcceptsAll
            && !isRepeat
            && !ShortcutResolver.OverridesAcceptAll(field.ProcessName, settings.ShortcutOverrides);
        double now = Now();
        if (!all && doubleTapApplies && _doubleTap.ConsumeDoubleTap(session.RequestId, now))
        {
            all = true;
        }
        _doubleTap.Reset();

        string visible = session.VisibleText;
        string chunk = all
            ? visible
            : settings.Granularity == AcceptanceGranularity.Phrase
                ? AcceptanceChunker.NextPhrase(visible, settings.AcceptPunctuationWithWord)
                : AcceptanceChunker.NextChunk(visible, settings.AcceptPunctuationWithWord);
        if (chunk.Length == 0) chunk = visible;
        // The space after a word may sit in the part of the prediction not shown yet.
        if (settings.AddSpaceAfterAccept) chunk = AcceptanceChunker.ConsumingTrailingSpace(chunk, session.PredictedText);

        var next = session.Advancing(chunk.Length);
        bool exhausted = next.IsExhausted;
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
            // continuation instead of leaking into the host. Only when a continuation will be asked for.
            if (SuggestionRequestFactory.ShouldGenerate(_expectedPreceding, settings.SuggestWithinWords))
            {
                OwnAcceptKeyBriefly(field, settings);
            }
        }
        else
        {
            _session = next;
            _sessionAcceptedAny = true;
            _overlay.Update(next.VisibleText);
            if (!all && doubleTapApplies) _doubleTap.RecordWordAccept(session.RequestId, now);
        }

        Chain(() => InsertAcceptedAsync(accepted));
    }

    /// <summary>Runs edits of the field one after another on the thread pool.</summary>
    private void Chain(Func<Task> edit) =>
        _insertChain = _insertChain.ContinueWith(_ => edit(), TaskScheduler.Default).Unwrap();

    private void OwnAcceptKeyBriefly(FocusedField field, SuggestionSettings settings) =>
        _ownership = new AcceptKeyOwnership(
            AcceptWordKeyFor(field.ProcessName),
            _clock.GetTimestamp() + (long)(PostExhaustionWindow.TotalSeconds * _clock.TimestampFrequency));

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
            if (field is null || !SameField(field, accepted.OfferedIn)
                || !SuggestionSessionReconciler.FollowsExpected(accepted.ExpectedPreceding, field.PrecedingText, PublishLagChars))
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
                    SetPublishBaseline(field, accepted.Insertion.Length);
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
    /// it is not cancelled, because a cancelled edit would leave the word deleted. An offered fix is
    /// only applied in the field it was offered in, still holding the text it was offered for.
    /// </summary>
    private async Task ApplyCorrectionAsync(
        TypoCorrection correction, bool requiresTrailingSpace, (FocusedField Field, string Preceding)? offered = null)
    {
        try
        {
            if (_focus.Refresh() is not { } field || field.HasSelection) return;
            if (offered is { } at && (!SameField(field, at.Field)
                || !SuggestionSessionReconciler.FollowsExpected(at.Preceding, field.PrecedingText, PublishLagChars)))
            {
                return;
            }
            var plan = TypoCorrectionReplacementPlanner.Plan(
                field.PrecedingText, correction.Word, correction.CorrectedWord, requiresTrailingSpace);
            if (plan is not { } fix) return;
            await _inserter.ReplaceAsync(field, fix.DeletingUtf16Count, fix.ReplacementText, CancellationToken.None)
                .ConfigureAwait(false);
            // The edit publishes asynchronously. Predicting before it lands could offer the same fix again.
            Post(() =>
            {
                SetPublishBaseline(field);
                SchedulePrediction(typed: null, requiresTextChange: true);
            });
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
        var after = _focus.Refresh(readHostText: true);
        RecordHistory(after);
        if (after is null || after.CaretRect.IsEmpty) return;
        Post(() =>
        {
            if (_session is not { } live || live.RequestId != requestId) return;
            _sessionField = after;
            _overlay.Show(live.VisibleText, after.CaretRect, StyleFor(after, live.Correction), after);
        });
    }

    /// <summary>Hands a fresh read of the field to the history recorder, which applies the same rules as suggestions.</summary>
    private void RecordHistory(FocusedField? field)
    {
        try
        {
            HistoryRecorder?.Observe(field, () => field is not null && IsAvailableIn(field));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Recording typing history failed.");
        }
    }

    private OverlayStyle StyleFor(FocusedField field, TypoCorrection? correction) =>
        OverlayStylePolicy.StyleFor(_settings.DisplayMode, field, correction is not null);

    private void PublishOffer()
    {
        var settings = _settings;
        var process = _sessionField?.ProcessName;
        _offer = new Offer(
            AcceptWordKeyFor(process),
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
        ResetAcceptKeyState();
        ClearSession();
        _prediction = null;
        _work.Cancel();
    }

    private void ResetAcceptKeyState()
    {
        _doubleTap.Reset();
        _ownership = null;
        _queuedAccept = false;
    }

    private double Now() => _clock.GetTimestamp() / (double)_clock.TimestampFrequency;

    private void Post(Action action)
    {
        if (SynchronizationContext.Current == _ui) action();
        else _ui.Post(_ => action(), null);
    }
}
