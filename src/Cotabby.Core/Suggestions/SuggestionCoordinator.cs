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
public sealed partial class SuggestionCoordinator : IAsyncDisposable
{
    // The hook fires before the host processes the key, so the field is polled
    // until the typed text shows up.
    private const int HostPublishFirstPollMs = 10;
    private const int HostPublishPollMs = 30;
    private const int HostPublishCeilingMs = 400;
    private const int PostInsertSettleMs = 60;

    /// <summary>How often a visible suggestion checks that the caret is still where it was offered.</summary>
    private const int SessionWatchMs = 250;

    /// <summary>How often it checks once no key has come for <see cref="SessionWatchIdleAfter"/>.</summary>
    private const int SessionWatchIdleMs = 1000;

    private static readonly TimeSpan SessionWatchIdleAfter = TimeSpan.FromSeconds(3);

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
    private readonly SuggestionAnchorCache _anchors;
    private readonly ImeCompositionTracker _ime = new();

    /// <summary>The field when the current composition began, or null while nothing composes.</summary>
    private FocusedField? _compositionStart;

    /// <summary>The least time between two reads of the input method's mode while keys are held for it.</summary>
    private static readonly TimeSpan ImeReobserveInterval = TimeSpan.FromMilliseconds(250);

    // When the mode was last read for held keys, and whether a read is under way. UI context only.
    private long _imeObservedAt;
    private bool _imeReobserving;
    private readonly SendOrPostCallback _handleEvent;

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

    /// <summary>When the read that set <see cref="_focusIdentity"/> was taken, as a timestamp of the clock.</summary>
    private long _identityReadAt;

    /// <summary>
    /// The focused field when its last read showed it closed to suggestions whatever is typed, so
    /// keys there start no read. UI context only.
    /// </summary>
    private ClosedField? _closedField;

    /// <summary>How long a closed field goes without a read while keys arrive, in case a focus event was missed.</summary>
    private static readonly TimeSpan ClosedFieldRecheck = TimeSpan.FromSeconds(2);

    private sealed record ClosedField(string Identity, long ReadAt);
    private AcceptedTail? _lastAcceptedTail;
    private bool _queuedAccept;

    /// <summary>Text an automatic fix was last applied to. It stops a fix the host ignored from repeating.</summary>
    private volatile string? _lastAutoFixedText;
    private Task _insertChain = Task.CompletedTask;

    /// <summary>The suggestion whose accept failed. Words of it accepted after the failure are not inserted.</summary>
    private string? _abandonedAccept;

    private bool _disposed;

    // What the field looked like at the last read, plus how many characters the
    // keys pressed since then should have added. Null means the next read is trusted as is.
    private FocusedField? _publishBaseline;
    private int _expectedDelta;
    private bool _publishAwaitsChange;
    private long _keySequence;

    // The field whose screen text was read at least once, and the key count when changed screen
    // text last asked for a new suggestion there. Both are touched on the UI context only.
    private string? _screenReadIdentity;
    private long _screenRegeneratedAt = -1;

    /// <summary>When the last key went down, as a timestamp of the clock. Written on the hook thread.</summary>
    private long _lastKeyAt;
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
        _anchors = new SuggestionAnchorCache(_clock);
        _handleEvent = state => HandleEvent((KeyboardEvent)state!);
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
            if (_screen is not null)
            {
                _screen.ExcerptChanged -= OnScreenExcerptChanged;
                _screen.FirstReadFinished -= OnScreenFirstReadFinished;
            }
            _screen = value;
            if (value is not null)
            {
                value.ExcerptChanged += OnScreenExcerptChanged;
                value.FirstReadFinished += OnScreenFirstReadFinished;
            }
        }
    }

    private ScreenContextSource? _screen;

    /// <summary>
    /// Evaluates a request's prompt ahead of time without generating, so the first suggestion in a
    /// newly focused field does not pay for the whole prompt. Null skips it.
    /// </summary>
    public Func<SuggestionRequest, CancellationToken, Task>? Prewarm { get; set; }

    private CancellationTokenSource? _prewarm;

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
                _closedField = null;
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
        PredictFromFreshRead();
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
        _prewarm?.Cancel();
        ScreenContext = null;
        _focus.Stop();
        _hook.Stop();
        await _work.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// The place a suggestion belongs to. Like upstream, the window title and the placeholder are
    /// part of it, so a chat app that keeps one composer while the conversation changes does not
    /// carry a suggestion, a late answer, a prefetch or a dismissal into the next conversation.
    /// The unsaved-changes mark of an editor's title does not count.
    /// </summary>
    private static string FocusIdentity(FocusedField field) => $"{ScreenContextSource.IdentityOf(field)}:{field.HelpText}";

    /// <summary>Dismissals hold within one field, so the same words stay offered in another. The memory lives in this process only.</summary>
    private static ulong DismissalScope(FocusedField field) =>
        (uint)HashCode.Combine(field.ProcessId, field.FieldId, field.HelpText, ScreenContextSource.TitleKey(field.WindowTitle));

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

    private double Now() => _clock.GetTimestamp() / (double)_clock.TimestampFrequency;

    /// <summary>Logs the failure of work nothing waits for, which would otherwise go unseen.</summary>
    private void LogFailure(Task task, string what)
    {
        if (task.IsCompletedSuccessfully) return;
        task.ContinueWith(
            (failed, state) => _logger.LogWarning(failed.Exception?.InnerException ?? failed.Exception, "{What} failed.", state),
            what, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private void Post(Action action)
    {
        if (SynchronizationContext.Current == _ui) action();
        else _ui.Post(static state => ((Action)state!)(), action);
    }
}
