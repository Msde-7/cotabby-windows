using System.Diagnostics;
using Cotabby.Core.Context;
using Cotabby.Core.Focus;
using Cotabby.Core.History;
using Cotabby.Core.Input;
using Cotabby.Core.Insertion;
using Cotabby.Core.Overlay;
using Cotabby.Core.Spelling;
using Cotabby.Core.Support;
using Cotabby.Core.Text;
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
    private readonly ImeCompositionTracker _ime = new();

    private volatile SuggestionSettings _settings = SuggestionSettings.Default;
    private bool? _batterySaverWasOn;
    private volatile Offer? _offer;
    private volatile AcceptKeyOwnership? _ownership;

    private SuggestionSession? _session;

    /// <summary>
    /// The request whose partials may still grow the visible suggestion. Null once the user types
    /// into it or accepts from it, which freezes it at what was streamed.
    /// </summary>
    private string? _streamingRequestId;

    /// <summary>What the streamed suggestion shows, so a partial only ever extends it.</summary>
    private string _streamRendered = "";
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

    /// <summary>
    /// Partials of one streamed request. They arrive on the decode thread at token rate, so only
    /// the newest waits and at most one drain is queued on the UI context at a time.
    /// </summary>
    private sealed class PartialStream(
        string requestId, FocusedField field, SuggestionSettings settings, long sequence, Stopwatch elapsed,
        CancellationToken ct)
    {
        public string RequestId { get; } = requestId;
        public FocusedField Field { get; } = field;
        public SuggestionSettings Settings { get; } = settings;
        public long Sequence { get; } = sequence;
        public Stopwatch Elapsed { get; } = elapsed;
        public CancellationToken Ct { get; } = ct;
        public readonly object Gate = new();
        public string? Pending;
        public bool DrainQueued;
        public volatile bool Finished;
    }

    /// <summary>What the hook thread may accept right now.</summary>
    private sealed record Offer(KeyBinding AcceptWord, KeyBinding AcceptAll);

    private sealed record AcceptKeyOwnership(KeyBinding AcceptWord, long ExpiresAt);

    private sealed record AcceptedTail(string Text, string PrecedingText);

    /// <summary>
    /// A generation for the text a suggestion leaves once used, started before it is. The text may
    /// still be generating; empty means it produced nothing.
    /// </summary>
    private sealed record PrefetchedContinuation(string FocusIdentity, string PrecedingText, Task<string> Text);

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

    /// <summary>Text recognized on screen around the focused field. Null runs without it.</summary>
    public ScreenContextSource? ScreenContext
    {
        get => _screen;
        set
        {
            if (_screen is not null) _screen.FirstExcerptReady -= OnFirstScreenExcerpt;
            _screen = value;
            if (value is not null) value.FirstExcerptReady += OnFirstScreenExcerpt;
        }
    }

    private ScreenContextSource? _screen;

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
            Post(() =>
            {
                CancelSession("settings changed");
                TrackScreen(_focus.Current, value);
            });
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
        ScreenContext = null;
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
            string previousIdentity = _focusIdentity;
            _focusIdentity = field is null ? "" : FocusIdentity(field);
            if (previousIdentity != _focusIdentity) _ime.Reset();
            _ime.Observe(field?.ImeLanguageId ?? 0);
            TrackScreen(field);
            bool sameHost = pid != 0 && (_focusedPid == 0 || pid == _focusedPid);
            _focusedPid = pid;
            // A host that names no field, or names the same one again, is the same place. Another
            // named field in the same app is a different place, so work for the old one stops.
            string fieldId = field?.FieldId ?? "";
            bool sameField = fieldId.Length == 0 || previousIdentity.Length == 0 || previousIdentity == _focusIdentity;
            if (sameHost && sameField && (_sessionField is null || _sessionField.FieldId == fieldId)) return;
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
                // The accept is queued before the work is cancelled. Cancelling can end a streamed
                // request at once, and its cleanup must not reach the UI context ahead of the accept.
                Post(() => Accept(all, ev.IsRepeat));
                _work.Cancel();
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
        if (HoldsForComposition(ev)) return;
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
                    _streamingRequestId = null;
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
    /// Holds Cotabby back while an input method composes text the app has not received yet. A
    /// suggestion would continue text that is about to change, and accepting one would land in
    /// the middle of the composition. Committing the composition asks for a suggestion again.
    /// </summary>
    private bool HoldsForComposition(KeyboardEvent ev)
    {
        bool committed = _ime.Key(ev);
        if (_ime.IsComposing)
        {
            if (_session is not null) CancelSession("the input method is composing");
            _prediction = null;
            _work.Cancel();
            return true;
        }
        if (!committed) return false;
        // The commit replaces the composition in the field, which a key count cannot follow.
        SetPublishBaseline(null);
        SchedulePrediction(typed: null);
        return true;
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
        long submittedAt = _clock.GetTimestamp();
        // The debounce runs alongside the wait for the host to show the key, not after it.
        _ = _work.Submit(
            ct => PredictAsync(requestId, settings, baseline, delta, typed, requiresTextChange, sequence, submittedAt, ct),
            TimeSpan.Zero);
    }

    private async Task PredictAsync(
        string requestId, SuggestionSettings settings, FocusedField? baseline, int delta, char? typed,
        bool requiresTextChange, long sequence, long submittedAt, CancellationToken ct)
    {
        TypingPrediction? prediction = null;
        PartialStream? stream = null;
        bool presented = false;
        try
        {
            var field = await WaitForHostPublishAsync(baseline, delta, typed, requiresTextChange, ct)
                .ConfigureAwait(false);
            // Whatever is left of the quiet time after a key, once the host has shown it. A key in
            // the meantime cancels this work, so a fast typist still starts no generation per key.
            var quiet = _work.Debounce - _clock.GetElapsedTime(submittedAt);
            if (quiet > TimeSpan.Zero) await Task.Delay(quiet, _clock, ct).ConfigureAwait(false);
            RecordHistory(field);
            if (field is null) return;
            Post(() =>
            {
                if (_keySequence == sequence) SetPublishBaseline(field);
                _ime.Observe(field.ImeLanguageId);
            });
            if (field.ImeLanguageId != 0 && _ime.IsComposing)
            {
                _logger.LogDebug("Request {ReqId} skipped: the input method is composing.", requestId);
                return;
            }

            TrackScreen(field, settings);
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
            if (await TakePrefetchedAsync(field, ct).ConfigureAwait(false) is { } prefetched)
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
                if (settings.StreamWhileGenerating) stream = new PartialStream(requestId, field, settings, sequence, stopwatch, ct);
                var predicting = prediction;
                var streaming = stream;
                Action<string>? onPartial = predicting is null && streaming is null ? null : partial =>
                {
                    if (predicting is not null && !predicting.Receive(partial, final: false))
                    {
                        RestartPrediction(predicting, "the model left what was typed");
                    }
                    if (streaming is not null) QueuePartial(streaming, partial);
                };
                text = await GenerateAsync(BuildRequest(field, requestId, settings), ct, onPartial).ConfigureAwait(false);
                // A cancelled request says nothing about how fast the engine is.
                if (!ct.IsCancellationRequested) UpdateDebounce(settings, (int)stopwatch.ElapsedMilliseconds);
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
            if (stream is not null)
            {
                stream.Finished = true;
                // A streamed suggestion the final answer did not confirm goes away with it.
                if (!presented) Post(() => EndStream(requestId, "the final answer was dropped"));
            }
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
            History?.HistoryExamples(field, settings.UsesEndpoint),
            settings.ScreenContext ? _screen?.For(field) : null);

    private void QueuePartial(PartialStream stream, string partial)
    {
        lock (stream.Gate)
        {
            stream.Pending = partial;
            if (stream.DrainQueued) return;
            stream.DrainQueued = true;
        }
        Post(() =>
        {
            string? pending;
            lock (stream.Gate)
            {
                pending = stream.Pending;
                stream.Pending = null;
                stream.DrainQueued = false;
            }
            if (pending is not null) ShowPartial(stream, pending);
        });
    }

    /// <summary>
    /// Shows a partial as a real, acceptable suggestion, so the user can take the first words the
    /// moment they appear. It passes the same checks as a final answer, minus a word still being
    /// generated, and only ever extends what shows.
    /// </summary>
    private void ShowPartial(PartialStream stream, string partial)
    {
        if (stream.Finished || stream.Ct.IsCancellationRequested || _keySequence != stream.Sequence) return;
        bool growing = _session is { } shown && shown.RequestId == stream.RequestId && _streamingRequestId == stream.RequestId;
        if (_session is not null && !growing) return;
        var field = stream.Field;
        if (_focusIdentity.Length > 0 && FocusIdentity(field) != _focusIdentity) return;
        if (PausedReason() is not null) return;
        // Mid-word, nothing flashes up between two keys, as with a final answer. The partial is tried
        // again after the pause unless a newer one has arrived by then.
        double pause = _cadence.RemainingDelay(FocusIdentity(field), field.PrecedingText, Now());
        if (pause > 0)
        {
            _ = RetryPartialAsync(stream, partial, pause);
            return;
        }

        string candidate = StreamedGhostTextPolicy.CompletedBufferedPrediction(
            partial, growing ? GraphemeString.CountOf(_streamRendered) : 0);
        if (string.IsNullOrWhiteSpace(candidate)) return;
        if (CompletionFinalizer.Finalize(candidate, field, stream.Settings, _engine.SpacingIsExact, _typos) is not { } completion)
        {
            return;
        }
        if (!StreamedGhostTextPolicy.IsRenderableExtension(completion.Predicted, growing ? _streamRendered : null)) return;

        if (!growing)
        {
            Present(stream.RequestId, completion, field, (int)stream.Elapsed.ElapsedMilliseconds, stream.Sequence, stream.Ct,
                streamed: true);
            return;
        }
        if (_dismissals.Suppresses(DismissalScope(field), field.PrecedingText, field.TrailingText, completion.Visible, Now()))
        {
            return;
        }
        var grown = _session! with { PredictedText = completion.Predicted, FirstOfferLength = completion.FirstOfferLength };
        _session = grown;
        _streamRendered = completion.Predicted;
        _overlay.Update(grown.VisibleText);
    }

    private async Task RetryPartialAsync(PartialStream stream, string partial, double pauseSeconds)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(pauseSeconds), _clock, stream.Ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        lock (stream.Gate)
        {
            if (stream.Pending is not null || stream.DrainQueued) return;
        }
        QueuePartial(stream, partial);
    }

    /// <summary>Clears a streamed suggestion that is still growing, once its request ends without a final answer.</summary>
    private void EndStream(string requestId, string reason)
    {
        if (_streamingRequestId != requestId) return;
        _streamingRequestId = null;
        if (_session?.RequestId != requestId) return;
        _logger.LogInformation("Suggestion cleared ({Reason}).", reason);
        ClearSession();
    }

    /// <summary>Keeps screen capture running for a field suggestions may appear in, and stops it everywhere else.</summary>
    private void TrackScreen(FocusedField? field, SuggestionSettings? settings = null)
    {
        if (_screen is not { } screen) return;
        settings ??= _settings;
        if (field is null || !settings.ScreenContext || field.IsSecure || !IsAvailableIn(field))
        {
            screen.Stop();
            return;
        }
        screen.Track(field, settings.UsesEndpoint);
    }

    /// <summary>
    /// A field focused with text already in it gets its first suggestion once the screen has been
    /// read, so that suggestion can use it. Typing in the meantime already asked for one.
    /// </summary>
    private void OnFirstScreenExcerpt(FocusedField field) => Post(() =>
    {
        if (_disposed || _session is not null || _prediction is not null || !_work.IsIdle) return;
        if (FocusIdentity(field) != _focusIdentity) return;
        SetPublishBaseline(null);
        SchedulePrediction(typed: null);
    });

    /// <summary>
    /// Generates what follows a suggestion before the user reaches its end, so the next one is
    /// ready the moment it is needed. Three cases qualify: a suggestion nearly typed through, a word
    /// ending on its own, and a spelling fix, where the next words are predicted as if the ending or
    /// fix were already in. It runs beside the current work, which it must not cancel, and only fills
    /// <see cref="_prefetched"/>, which nothing shows until the live text matches it.
    /// </summary>
    /// <param name="atOffer">True when called as the suggestion first shows.</param>
    private void PrefetchContinuation(SuggestionSession session, bool atOffer = false)
    {
        if (_sessionPrefetched || _sessionField is not { } offeredIn || session.IsExhausted || !_engine.IsReady) return;
        var settings = _settings;
        if (ContinuationTarget(session, settings, atOffer) is not { } preceding) return;
        if (!SuggestionRequestFactory.ShouldGenerate(preceding, offeredIn.TrailingText, settings.SuggestWithinWords)) return;
        _sessionPrefetched = true;

        var field = offeredIn with { Text = preceding + offeredIn.TrailingText, CaretOffset = preceding.Length };
        string identity = FocusIdentity(field);
        CancelPrefetch();
        var cts = _prefetch = new CancellationTokenSource();
        var text = Task.Run(async () =>
        {
            try
            {
                var request = BuildRequest(field, "pre_" + Guid.NewGuid().ToString("N")[..8], settings);
                string generated = await GenerateAsync(request, cts.Token).ConfigureAwait(false);
                if (generated.Length > 0) _logger.LogDebug("Prefetched the continuation of {ReqId}.", session.RequestId);
                return cts.IsCancellationRequested ? "" : generated;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Prefetching the continuation of {ReqId} failed.", session.RequestId);
                return "";
            }
        });
        _prefetched = new PrefetchedContinuation(identity, preceding, text);
    }

    /// <summary>
    /// The text before the caret once the suggestion is used, built from the session rather than the
    /// field, which may not have published the last keys yet. Null when nothing should be prefetched.
    /// </summary>
    private string? ContinuationTarget(SuggestionSession session, SuggestionSettings settings, bool atOffer)
    {
        if (session.Correction is { } correction)
        {
            // An unconfirmed fix is new, speculative text, so it stays on this PC like upstream's.
            if (settings.UsesEndpoint) return null;
            return TypoCorrectionReplacementPlanner.Plan(
                    _expectedPreceding, correction.Word, correction.CorrectedWord, requiresTrailingSpace: false) is { } fix
                ? _expectedPreceding[..^fix.DeletingUtf16Count] + fix.ReplacementText
                : null;
        }
        if (session.PredictedText.Length <= ContinuationPrefetchChars) return _expectedPreceding + session.PredictedText;
        // A lone word ending: the words after it are a guess the model has not made yet.
        bool wordEnding = atOffer && !settings.UsesEndpoint
            && !session.PredictedText.Any(char.IsWhiteSpace)
            && CaretWordContext.UnfinishedWord(_expectedPreceding) is not null;
        return wordEnding ? _expectedPreceding + session.PredictedText : null;
    }

    /// <summary>
    /// The prefetched continuation when it was generated for this very text, waiting for it if it is
    /// still running. Used once. Null when there is none for this text or it produced nothing.
    /// </summary>
    private async Task<string?> TakePrefetchedAsync(FocusedField field, CancellationToken ct)
    {
        var prefetched = Interlocked.Exchange(ref _prefetched, null);
        if (prefetched is null || prefetched.FocusIdentity != FocusIdentity(field)) return null;
        string expected = SuggestionSessionReconciler.SpaceNormalized(prefetched.PrecedingText);
        string live = SuggestionSessionReconciler.SpaceNormalized(field.PrecedingText);
        // The space added after accepting, or typed by the user, ends the same word. It is the one
        // difference allowed, and it takes the place of the continuation's own leading space.
        bool joinedBySpace = live == expected + " " && !expected.EndsWith(' ');
        if (live != expected && !joinedBySpace) return null;
        string text = await prefetched.Text.WaitAsync(ct).ConfigureAwait(false);
        if (joinedBySpace) text = text.StartsWith(' ') ? text[1..] : "";
        return text.Length == 0 ? null : text;
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

    /// <param name="streamed">True for a partial that later partials and the final answer may grow.</param>
    private void Present(
        string requestId, FinalCompletion completion, FocusedField field, int latencyMs, long sequence,
        CancellationToken ct, TypoCorrection? correction = null, bool streamed = false)
    {
        // The final answer of a streamed request takes over the suggestion its partials built.
        if (!streamed && _session is { } shown && shown.RequestId == requestId && _streamingRequestId == requestId)
        {
            ReplaceStream(completion, field);
            return;
        }
        // A key handled after the work finished can no longer cancel it, so the sequence is checked too.
        if (ct.IsCancellationRequested || _session is not null || _keySequence != sequence) return;
        // Focus may have moved to another field in the same app while this was decided.
        if (_focusIdentity.Length > 0 && FocusIdentity(field) != _focusIdentity) return;

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
        _streamingRequestId = streamed ? requestId : null;
        _streamRendered = streamed ? completion.Predicted : "";
        PublishOffer();
        _overlay.Show(text, field.CaretRect, StyleFor(field, correction), field);
        if (correction is null) Metrics.RecordShown(latencyMs);
        _logger.LogInformation("Suggestion shown req={ReqId} latency={Ms}ms chars={Chars}", requestId, latencyMs, text.Length);
        _logger.LogDebug("Suggestion text req={ReqId} \"{Text}\"", requestId, LogText.Escape(text));

        _ = WatchSessionAsync(requestId);
        // A partial is still growing, so the words after it are not known yet.
        if (!streamed) PrefetchContinuation(session, atOffer: true);

        // A queued press only counts while the key is still owned. Later it would accept unseen text.
        bool acceptQueued = _queuedAccept && _ownership is { } owned && _clock.GetTimestamp() < owned.ExpiresAt;
        _ownership = null;
        _queuedAccept = false;
        if (acceptQueued && correction is null) Accept(all: false, isRepeat: true);
    }

    /// <summary>
    /// Swaps a streamed suggestion for the final answer, which was checked against the same text.
    /// A final answer that no longer begins with what shows leaves the suggestion as it is.
    /// </summary>
    private void ReplaceStream(FinalCompletion completion, FocusedField field)
    {
        _streamingRequestId = null;
        if (_session is not { } current) return;
        if (!completion.Predicted.StartsWith(current.VisibleText, StringComparison.Ordinal)
            || _dismissals.Suppresses(DismissalScope(field), field.PrecedingText, field.TrailingText, completion.Visible, Now()))
        {
            return;
        }
        var final = current with { PredictedText = completion.Predicted, FirstOfferLength = completion.FirstOfferLength };
        _session = final;
        _overlay.Update(final.VisibleText);
        PrefetchContinuation(final, atOffer: true);
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
        && !live.HasSelection
        && SuggestionSessionReconciler.SpaceNormalized(live.TrailingText)
            == SuggestionSessionReconciler.SpaceNormalized(offeredIn.TrailingText)
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
        _streamingRequestId = null;
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
        var accepted = new AcceptedChunk(
            session.RequestId, field, expected, chunk, insertion, exhausted, firstChunk, _pendingInsertChars);
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
        bool Exhausted, bool FirstChunk, int PendingBefore);

    private async Task InsertAcceptedAsync(AcceptedChunk accepted)
    {
        try
        {
            // The text goes where the caret is, so it is only sent if the caret is still where the
            // suggestion was shown.
            var field = _focus.Refresh();
            // Chunks accepted just before this one may not have reached the field yet.
            if (field is null || !SameField(field, accepted.OfferedIn)
                || !SuggestionSessionReconciler.FollowsExpected(
                    accepted.ExpectedPreceding, field.PrecedingText, PublishLagChars + accepted.PendingBefore))
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
        _streamingRequestId = null;
        _streamRendered = "";
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
