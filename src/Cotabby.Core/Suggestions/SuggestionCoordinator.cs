using System.Diagnostics;
using Cotabby.Core.Focus;
using Cotabby.Core.Input;
using Cotabby.Core.Insertion;
using Cotabby.Core.Overlay;
using Microsoft.Extensions.Logging;

namespace Cotabby.Core.Suggestions;

/// <summary>
/// Top-level orchestrator that ties the keyboard hook, focus tracker, suggestion
/// engine, overlay, and inserter together. Mirrors the macOS
/// <c>SuggestionCoordinator</c>; this single class corresponds to the
/// <c>+Lifecycle</c>, <c>+Input</c>, <c>+Prediction</c>, and <c>+Acceptance</c>
/// extension files in the macOS port.
/// </summary>
/// <remarks>
/// Threading: keyboard events arrive on the hook thread, UIA events on a UIA
/// pool thread. The coordinator marshals all session mutations through a single
/// SynchronizationContext (the UI thread) so the state machine never sees racy
/// reads of <see cref="_session"/>. Pure decision logic stays in
/// <see cref="SuggestionSessionReconciler"/> / <see cref="SuggestionAvailability"/>
/// so this class only owns wiring.
/// </remarks>
public sealed class SuggestionCoordinator : IAsyncDisposable
{
    private readonly IKeyboardHook _hook;
    private readonly IFocusTracker _focus;
    private readonly ISuggestionEngine _engine;
    private readonly IOverlayPresenter _overlay;
    private readonly ITextInserter _inserter;
    private readonly SuggestionWorkController _work;
    private readonly ILogger<SuggestionCoordinator> _logger;
    private readonly SynchronizationContext _ui;

    private SuggestionSession? _session;
    private int _sessionPid;
    private bool _enabled = true;
    private bool _disposed;
    /// <summary>True while a generation is past its debounce and the engine is producing tokens.</summary>
    private bool _generationInFlight;

    public SuggestionCoordinator(
        IKeyboardHook hook,
        IFocusTracker focus,
        ISuggestionEngine engine,
        IOverlayPresenter overlay,
        ITextInserter inserter,
        SuggestionWorkController work,
        ILogger<SuggestionCoordinator> logger,
        SynchronizationContext ui)
    {
        _hook = hook;
        _focus = focus;
        _engine = engine;
        _overlay = overlay;
        _inserter = inserter;
        _work = work;
        _logger = logger;
        _ui = ui;
    }

    /// <summary>Whether suggestions are globally enabled. Tray toggle binds here.</summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value) return;
            _enabled = value;
            if (!value) CancelSession("disabled");
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
            if (_session is null) return;
            // Compare host process, not AutomationElement reference: UIA returns
            // a fresh AutomationElement instance on every focus event even when
            // focus stayed on the same conceptual field (Monaco/Electron in
            // particular re-fire focus when their internal AX tree mutates),
            // and ReferenceEquals on those instances is always false.
            if (field is null || field.ProcessId != _sessionPid)
            {
                CancelSession("focus changed to different process");
            }
        });
    }

    private void OnKeyEvent(object? sender, KeyEventArgs args)
    {
        if (!_enabled) return;

        _logger.LogDebug("Key kind={Kind} char={Char} down={Down} mod={Mod} session={HasSession}",
            args.Event.Kind, (int)args.Event.Character, args.Event.IsKeyDown,
            args.Event.HasNonShiftModifier, _session is not null);

        // Tab acceptance must be decided synchronously on the hook thread —
        // returning true here is the only way to eat the keystroke before the
        // host app sees it. We read the session reference into a local so we
        // don't race against a concurrent UI-thread mutation.
        var snap = _session;
        var outcome = SuggestionSessionReconciler.Apply(snap, args.Event);
        if (outcome.Outcome == SuggestionSessionReconciler.Outcome.Accept)
        {
            args.Suppress = true;
            Post(() => AcceptAsync().ContinueWith(LogFault, TaskScheduler.Default));
            return;
        }

        Post(() => HandleEvent(args.Event));
    }

    private void HandleEvent(KeyboardEvent ev)
    {
        if (_session is not null)
        {
            var result = SuggestionSessionReconciler.Apply(_session, ev);
            switch (result.Outcome)
            {
                case SuggestionSessionReconciler.Outcome.AdvanceVisible when result.Next is { } next:
                    _session = next;
                    _overlay.Update(next.VisibleText);
                    if (next.VisibleText.Length == 0 && next.IsComplete)
                    {
                        CancelSession("consumed");
                    }
                    return;
                case SuggestionSessionReconciler.Outcome.Cancel:
                    CancelSession("reconciler");
                    // fall through to potentially trigger a new request
                    break;
                case SuggestionSessionReconciler.Outcome.Ignore:
                    return;
            }
        }

        if (SuggestionAvailability.ShouldCancel(ev))
        {
            CancelSession("availability cancel");
            return;
        }

        var field = _focus.Refresh();
        if (field is null)
        {
            _logger.LogDebug("No focused field at trigger-decision time.");
            return;
        }
        if (!SuggestionAvailability.ShouldRequest(field, ev))
        {
            _logger.LogDebug("ShouldRequest false: secure={Sec} textLen={Len} host={Host}",
                field.IsSecure, field.Text.Length, field.ProcessName);
            return;
        }

        // One-at-a-time generation: if the engine is already producing tokens,
        // don't cancel and restart — that's why fast typing produced zero
        // suggestions before. Let the in-flight generation finish; the chunk
        // handler re-reads the live field and fast-forwards through whatever
        // the user typed in the meantime.
        if (_generationInFlight)
        {
            _logger.LogDebug("Skip trigger: generation already in flight.");
            return;
        }

        _logger.LogInformation("Triggering suggestion: host={Host} textLen={Len} caret={Caret}",
            field.ProcessName, field.Text.Length, field.CaretOffset);
        TriggerGeneration(field);
    }

    private void TriggerGeneration(FocusedField field)
    {
        var reqId = "req_" + Guid.NewGuid().ToString("N")[..8];
        var request = SuggestionRequestFactory.Build(field, reqId);
        var anchor = field.CaretRect;
        var element = field.ElementHandle;

        _logger.LogInformation("Trigger request {ReqId} prefix={Len} host={Host} anchor={X},{Y} {W}x{H}",
            reqId, request.Prefix.Length, request.HostApp,
            (int)anchor.X, (int)anchor.Y, (int)anchor.Width, (int)anchor.Height);

        _generationInFlight = true;
        _ = _work.Submit(async ct =>
        {
            try
            {
                if (!_engine.IsReady)
                {
                    _logger.LogWarning("Engine not ready when work fired for {ReqId}.", reqId);
                    return;
                }

                var sw = Stopwatch.StartNew();
                string accumulated = string.Empty;
                bool overlayShown = false;

            await foreach (var chunk in _engine.GenerateAsync(request, ct).ConfigureAwait(false))
            {
                if (ct.IsCancellationRequested) return;
                accumulated += chunk.Text;

                if (!chunk.IsFinal && string.IsNullOrEmpty(chunk.Text)) continue;

                Post(() =>
                {
                    if (_session is not null && _session.RequestId != reqId)
                    {
                        _logger.LogDebug("Chunk for stale req {ReqId} (current session is {Cur}); dropping.",
                            reqId, _session.RequestId);
                        return;
                    }

                    if (_session is null)
                    {
                        if (string.IsNullOrEmpty(accumulated))
                        {
                            _logger.LogDebug("First chunk empty for {ReqId}; skipping show.", reqId);
                            return;
                        }

                        // Re-read the live caret so the overlay anchors to where
                        // the user *is*, not where they were ~1s ago when the
                        // request fired. We deliberately do NOT try to verify
                        // the user typed something matching — just show the
                        // suggestion; the reconciler advances or cancels on the
                        // next keystroke based on whether it matches.
                        var live = _focus.Refresh();
                        ScreenRect liveAnchor;
                        int livePid;
                        if (live is not null && !live.CaretRect.IsEmpty
                            && live.ProcessId == field.ProcessId)
                        {
                            liveAnchor = live.CaretRect;
                            livePid = live.ProcessId;
                        }
                        else
                        {
                            liveAnchor = anchor;
                            livePid = field.ProcessId;
                        }

                        _session = new SuggestionSession
                        {
                            RequestId = reqId,
                            ElementHandle = element,
                            OriginalPrefix = request.Prefix,
                            ConsumedChars = 0,
                            VisibleText = accumulated,
                            IsComplete = chunk.IsFinal,
                            AnchorRect = liveAnchor,
                        };
                        _sessionPid = livePid;
                        _overlay.Show(accumulated, liveAnchor);
                        overlayShown = true;
                        _logger.LogInformation(
                            "Overlay shown req={ReqId} text=\"{Preview}\" anchor={X},{Y}",
                            reqId, Preview(accumulated), (int)liveAnchor.X, (int)liveAnchor.Y);
                    }
                    else
                    {
                        _session = _session with
                        {
                            VisibleText = accumulated,
                            IsComplete = chunk.IsFinal,
                        };
                        if (overlayShown) _overlay.Update(accumulated);
                        else { _overlay.Show(accumulated, _session.AnchorRect); overlayShown = true; }
                    }
                });
            }

                sw.Stop();
                _logger.LogInformation("Request {ReqId} finished in {Ms} ms.", reqId, sw.ElapsedMilliseconds);
            }
            finally
            {
                Post(() => _generationInFlight = false);
            }
        });
    }

    private async Task AcceptAsync()
    {
        var session = _session;
        if (session is null || session.VisibleText.Length == 0) return;

        var field = _focus.Refresh();
        if (field is null) { CancelSession("no field at accept"); return; }

        var text = session.VisibleText;
        CancelSession("accepted");
        try
        {
            await _inserter.InsertAsync(field, text, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Insertion threw.");
        }
    }

    private void CancelSession(string reason)
    {
        if (_session is null && !_generationInFlight) return;
        _logger.LogInformation("Cancel session ({Reason}).", reason);
        _session = null;
        _sessionPid = 0;
        _generationInFlight = false;
        _overlay.Hide();
        _work.Cancel();
    }

    private static string Preview(string s) => s.Length <= 40 ? s : s[..40] + "…";

    private void Post(Action action)
    {
        if (SynchronizationContext.Current == _ui) action();
        else _ui.Post(_ => action(), null);
    }

    private void LogFault(Task t)
    {
        if (t.Exception is { } ex) _logger.LogError(ex, "Background task faulted.");
    }
}
