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
    /// <summary>Process id of the host that owned focus when the in-flight request fired.</summary>
    private int _inFlightPid;
    /// <summary>
    /// Volatile flag the hook thread reads to decide whether to synchronously
    /// suppress Tab. A separate primitive from <see cref="_session"/> because
    /// .NET's memory model doesn't guarantee reference-field visibility across
    /// threads without explicit fences. Volatile.Write/Read on this int gives
    /// us a portable acquire/release ordering — Tab acceptance becomes
    /// deterministic instead of dropping every ~3rd attempt to a stale null.
    /// </summary>
    private int _hasVisibleSession; // 0 or 1; written with Volatile.Write

    /// <summary>
    /// Snapshot of exactly what the overlay last rendered, written on every
    /// Show/Update and read by the hook-thread Tab fast-path. The streaming
    /// work can post a chunk-arrival action that mutates <see cref="_session"/>
    /// between the hook firing Tab and the dispatcher running AcceptAsync;
    /// without snapshotting the displayed text we'd insert a later/different
    /// completion than the one the user accepted with their eyes. Writes
    /// happen on the UI thread; the hook thread does a release-acquire read.
    /// </summary>
    private string? _displayedTextSnapshot;

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
            // Compare host process, not AutomationElement reference: UIA returns
            // a fresh AutomationElement instance on every focus event even when
            // focus stayed on the same conceptual field (Monaco/Electron in
            // particular re-fire focus when their internal AX tree mutates),
            // and ReferenceEquals on those instances is always false.
            int newPid = field?.ProcessId ?? 0;

            if (_session is not null && (field is null || newPid != _sessionPid))
            {
                CancelSession("focus changed to different process");
            }

            // Also abort any in-flight work whose request was for a different
            // process — otherwise the in-flight generation keeps streaming for
            // the OLD window's prefix and eventually pops a stale overlay over
            // the new window. The work's Post handler also re-checks before
            // touching state, but cancelling immediately frees CPU sooner.
            if (_generationInFlight && _inFlightPid != 0 && newPid != _inFlightPid)
            {
                _logger.LogInformation("Focus moved (oldPid={Old}, newPid={New}); cancelling in-flight work.",
                    _inFlightPid, newPid);
                _work.Cancel();
                _generationInFlight = false;
                _inFlightPid = 0;
            }
        });
    }

    private void OnKeyEvent(object? sender, KeyEventArgs args)
    {
        if (!_enabled) return;

        // Read the cross-thread visibility flag (release-acquire ordered).
        bool hasSession = Volatile.Read(ref _hasVisibleSession) != 0;

        _logger.LogDebug("Key kind={Kind} char={Char} down={Down} mod={Mod} session={HasSession}",
            args.Event.Kind, (int)args.Event.Character, args.Event.IsKeyDown,
            args.Event.HasNonShiftModifier, hasSession);

        // Tab + visible session => accept. We suppress synchronously here
        // because that's the only way to eat the Tab before it reaches the
        // host app, and Post AcceptAsync to the dispatcher so it runs single-
        // threaded with the rest of the state machine.
        if (hasSession && args.Event.Kind == KeyKind.Tab && args.Event.IsKeyDown)
        {
            args.Suppress = true;
            // Snapshot the displayed text right now, on the hook thread. Any
            // chunk that streams in between this point and AcceptAsync running
            // would otherwise mutate _session.VisibleText and cause us to
            // insert something the user didn't accept. Stop the engine
            // immediately for the same reason.
            string textToInsert = Volatile.Read(ref _displayedTextSnapshot) ?? string.Empty;
            _work.Cancel();
            _logger.LogInformation(
                "Tab fast-path: snapshot len={Len} text=\"{Text}\" hex={Hex}",
                textToInsert.Length, EscapeFull(textToInsert), HexDump(textToInsert));
            Post(() => AcceptAsync(textToInsert).ContinueWith(LogFault, TaskScheduler.Default));
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
                case SuggestionSessionReconciler.Outcome.Accept:
                    // The hook-thread fast-path missed this Tab (volatile flag
                    // wasn't yet set when the key fired). Accept on the pump
                    // where state is consistent. The host already received the
                    // raw Tab — that's a one-frame cosmetic glitch we accept
                    // in exchange for deterministic insertion. Snapshot the
                    // displayed text now so we still insert what's on screen
                    // even though we missed the fast-path snapshot.
                    {
                        string acceptText = _displayedTextSnapshot ?? _session.VisibleText;
                        AcceptAsync(acceptText).ContinueWith(LogFault, TaskScheduler.Default);
                    }
                    return;
                case SuggestionSessionReconciler.Outcome.AdvanceVisible when result.Next is { } next:
                    _session = next;
                    if (next.VisibleText.Length == 0)
                    {
                        Volatile.Write(ref _hasVisibleSession, 0);
                    }
                    Volatile.Write(ref _displayedTextSnapshot, next.VisibleText);
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

        // Match upstream Cotabby behavior: every new keystroke cancels the
        // prior debounce/work and reschedules a fresh one. Without this, the
        // FIRST keystroke captures the prefix for the entire typing burst and
        // ~5s later we surface a completion for context that no longer exists
        // ("ork" appearing after the user already typed "ork" themselves and
        // moved on). The cancel-prior path lets Submit replace pending work,
        // and the work function snapshots the *live* focus at debounce expiry.
        _logger.LogInformation("Triggering suggestion: host={Host} textLen={Len} caret={Caret}",
            field.ProcessName, field.Text.Length, field.CaretOffset);
        TriggerGeneration(field);
    }

    private void TriggerGeneration(FocusedField fieldAtTrigger)
    {
        var reqId = "req_" + Guid.NewGuid().ToString("N")[..8];
        var pid = fieldAtTrigger.ProcessId;

        // Capture the prefix synchronously at trigger time. The PostAsync +
        // _focus.Refresh() round-trip we used to do inside the work function
        // adds 100-300ms of UIA overhead per request, which combined with
        // 250ms of debounce means the next keystroke almost always cancels
        // the engine BEFORE it can produce a chunk. Now we lock the snapshot
        // here; if the user keeps typing during the debounce window, Submit's
        // cancel-prior takes care of dropping the stale request.
        var request = SuggestionRequestFactory.Build(fieldAtTrigger, reqId);
        var anchor = fieldAtTrigger.CaretRect;
        var element = fieldAtTrigger.ElementHandle;
        var field = fieldAtTrigger;

        _logger.LogInformation(
            "Scheduled request {ReqId} pid={Pid} prefix={Len} host={Host} anchor={X},{Y}",
            reqId, pid, request.Prefix.Length, request.HostApp,
            (int)anchor.X, (int)anchor.Y);

        _generationInFlight = true;
        _inFlightPid = pid;
        _ = _work.Submit(async ct =>
        {
            _logger.LogInformation("Firing request {ReqId} (post-debounce).", reqId);

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
                var lastPosted = DateTime.MinValue;
                var minInterval = TimeSpan.FromMilliseconds(50);

                // Watchdog: cap any single generation at 4 seconds. Without
                // this a hung engine call leaves _generationInFlight=true
                // forever and the next keystroke is silently dropped — that's
                // the "Notepad randomly stops showing suggestions" symptom.
                // 4s comfortably covers prompt eval + 24-token decode on the
                // 1.5B Q4 model on CPU; anything past that is a wedge to be
                // killed, not a slow-but-recoverable generation.
                using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(4));
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, watchdog.Token);
                var combinedToken = linked.Token;

            int chunkSeq = 0;
            await foreach (var chunk in _engine.GenerateAsync(request, combinedToken).ConfigureAwait(false))
            {
                if (ct.IsCancellationRequested)
                {
                    _logger.LogInformation(
                        "Req {ReqId} aborted by cancel after {Chunks} chunks ({Chars} chars).",
                        reqId, chunkSeq, accumulated.Length);
                    return;
                }
                chunkSeq++;
                accumulated += chunk.Text;

                // Defense-in-depth — guard a second time at the coordinator
                // level. The engine has its own char-run + pattern + 80-char
                // guards; if any of them fails (e.g. the engine yields a
                // chunk that pushes accumulated past the limit) we still
                // refuse to display or insert past 80 visible chars total
                // and a 5+ same-char tail run.
                if (accumulated.Length > 80)
                {
                    _logger.LogInformation("Req {ReqId}: coordinator hard-cap at {Len} chars; truncating.",
                        reqId, accumulated.Length);
                    accumulated = accumulated[..80];
                    // Stop accumulating any further chunks for this request.
                    combinedToken = new CancellationToken(true);
                }
                else
                {
                    int runStart = FindInteriorRunStart(accumulated, 4);
                    if (runStart >= 0)
                    {
                        _logger.LogInformation(
                            "Req {ReqId}: coordinator detected interior char run at {Idx}; truncating from {Old} → {New} chars.",
                            reqId, runStart, accumulated.Length, runStart);
                        accumulated = accumulated[..runStart].TrimEnd();
                        combinedToken = new CancellationToken(true);
                    }
                }

                if (!chunk.IsFinal && string.IsNullOrEmpty(chunk.Text)) continue;

                // Always Post the first chunk (so overlay appears ASAP) and the
                // final chunk (so trailing text isn't dropped). Throttle the
                // middle.
                bool isFirstShow = !overlayShown;
                if (!isFirstShow && !chunk.IsFinal)
                {
                    var now = DateTime.UtcNow;
                    if (now - lastPosted < minInterval) continue;
                    lastPosted = now;
                }
                else
                {
                    lastPosted = DateTime.UtcNow;
                }

                var ctSnapshot = combinedToken;
                Post(() =>
                {
                    if (ctSnapshot.IsCancellationRequested) return;

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

                        // Re-read the live caret to anchor the overlay where
                        // the user *is*. The work CTS is already cancelled by
                        // OnFocusChanged if the user Alt-Tabbed away, so we
                        // don't double-check the PID here — the
                        // ctSnapshot.IsCancellationRequested guard above
                        // covers that. Trying to be too strict here was
                        // dropping legitimate completions when UIA returned a
                        // null or zero-rect for a frame.
                        var live = _focus.Refresh();
                        var liveAnchor = (live is not null && !live.CaretRect.IsEmpty)
                            ? live.CaretRect : anchor;
                        int livePid = live?.ProcessId ?? field.ProcessId;

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
                        Volatile.Write(ref _hasVisibleSession, 1);
                        Volatile.Write(ref _displayedTextSnapshot, accumulated);
                        _overlay.Show(accumulated, liveAnchor);
                        overlayShown = true;
                        _logger.LogInformation(
                            "Overlay shown req={ReqId} text=\"{Text}\" hex={Hex} anchor={X},{Y}",
                            reqId, EscapeFull(accumulated), HexDump(accumulated),
                            (int)liveAnchor.X, (int)liveAnchor.Y);
                    }
                    else
                    {
                        _session = _session with
                        {
                            VisibleText = accumulated,
                            IsComplete = chunk.IsFinal,
                        };
                        Volatile.Write(ref _displayedTextSnapshot, accumulated);
                        if (overlayShown) _overlay.Update(accumulated);
                        else { _overlay.Show(accumulated, _session.AnchorRect); overlayShown = true; }
                        _logger.LogInformation(
                            "Overlay updated req={ReqId} text=\"{Text}\" hex={Hex}",
                            reqId, EscapeFull(accumulated), HexDump(accumulated));
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

    private async Task AcceptAsync(string textToInsert)
    {
        var session = _session;
        if (session is null)
        {
            _logger.LogInformation("AcceptAsync: session null, nothing to insert.");
            return;
        }
        if (string.IsNullOrEmpty(textToInsert))
        {
            _logger.LogInformation("AcceptAsync: snapshot empty (req={ReqId}), skip insert.",
                session.RequestId);
            return;
        }

        var field = _focus.Refresh();
        if (field is null)
        {
            _logger.LogInformation("AcceptAsync: no focused field at accept time, cancel.");
            CancelSession("no field at accept");
            return;
        }

        // FINAL FIREWALL before SendInput. If by some path a degenerate
        // suggestion slipped through both the engine and the coordinator's
        // streaming guards, trim it here. Better to insert nothing than
        // 30 consecutive 't's into the user's editor.
        var text = StripDegenerateTail(textToInsert);
        if (string.IsNullOrEmpty(text))
        {
            _logger.LogInformation("AcceptAsync: snapshot was all degenerate after trim, skip insert.");
            CancelSession("accepted");
            return;
        }
        // Detailed monitoring: log BOTH what the snapshot held AND what we
        // pass to the inserter, byte-for-byte. The user-visible "spliced
        // suggestion" symptoms can be diagnosed by joining these against the
        // SendInput delivery count in the inserter log.
        _logger.LogInformation("AcceptAsync: snapshot=\"{Snap}\" trimmed=\"{Trim}\" hex={Hex}",
            EscapeFull(textToInsert), EscapeFull(text), HexDump(text));
        CancelSession("accepted");
        try
        {
            var ok = await _inserter.InsertAsync(field, text, CancellationToken.None).ConfigureAwait(false);
            _logger.LogInformation("AcceptAsync: inserter returned ok={Ok}", ok);

            // Verify what actually landed in the host. Give the host's input
            // queue ~30ms to publish the WM_CHARs back into AX/UIA, then re-read
            // the focused field. The diff between {inserted, prefixBefore} and
            // {afterText} is the actual on-screen result — if it differs from
            // what we tried to type, the bug isn't in the coordinator/snapshot
            // but in SendInput delivery or host re-interpretation.
            await Task.Delay(60).ConfigureAwait(false);
            try
            {
                var after = _focus.Refresh();
                if (after is not null)
                {
                    int prefixLen = field.Text.Length;
                    string tail = after.Text.Length > prefixLen
                        ? after.Text[prefixLen..Math.Min(after.Text.Length, prefixLen + text.Length + 32)]
                        : "";
                    _logger.LogInformation(
                        "AcceptAsync: post-insertion verify pid={Pid} hostTextLen={Len} caret={Caret} tailAfterInsert=\"{Tail}\" hex={Hex}",
                        after.ProcessId, after.Text.Length, after.CaretOffset,
                        EscapeFull(tail), HexDump(tail));
                }
                else
                {
                    _logger.LogInformation("AcceptAsync: post-insertion verify FAILED — no focused field returned.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "AcceptAsync: post-insertion verify threw.");
            }
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
        _inFlightPid = 0;
        Volatile.Write(ref _hasVisibleSession, 0);
        Volatile.Write(ref _displayedTextSnapshot, null);
        _overlay.Hide();
        _work.Cancel();
    }

    private static string Preview(string s) => s.Length <= 40 ? s : s[..40] + "…";

    /// <summary>
    /// Escape control chars and newlines so a single-line log entry shows the
    /// FULL string verbatim. Used by the detailed-monitoring path so we can
    /// reconstruct exactly what the engine produced vs what the inserter sent
    /// vs what the host received.
    /// </summary>
    private static string EscapeFull(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        var sb = new System.Text.StringBuilder(s.Length + 8);
        foreach (var c in s)
        {
            switch (c)
            {
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '"':  sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                default:
                    if (c < 0x20 || c == 0x7F) sb.Append($"\\x{(int)c:X2}");
                    else sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }

    /// <summary>UTF-16 hex dump of <paramref name="s"/>. Lets us spot doubled
    /// chars, invisible whitespace, or off-by-one differences between snapshot
    /// and inserter at a glance.</summary>
    private static string HexDump(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new System.Text.StringBuilder(s.Length * 5);
        for (int i = 0; i < s.Length; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(((int)s[i]).ToString("X4"));
        }
        return sb.ToString();
    }

    /// <summary>True if the tail of <paramref name="s"/> ends with 5+ identical
    /// non-whitespace chars — a degenerate run that should be stripped.</summary>
    private static bool HasTrailingCharRun(string s, int threshold)
    {
        if (s.Length <= threshold) return false;
        char last = s[^1];
        if (last == ' ' || last == '\t' || last == '\n' || last == '\r') return false;
        int count = 0;
        for (int i = s.Length - 1; i >= 0 && count <= threshold; i--)
        {
            if (s[i] != last) return false;
            count++;
        }
        return count > threshold;
    }

    /// <summary>Find the first index of a run of 5+ identical non-whitespace
    /// chars anywhere in <paramref name="s"/>, or -1 if none. Used to truncate
    /// the suggestion before an interior degenerate run reaches the host —
    /// degeneration that the engine's tail-only check missed when the run was
    /// split across many small chunks.</summary>
    private static int FindInteriorRunStart(string s, int threshold)
    {
        if (s.Length <= threshold) return -1;
        int runStart = 0;
        for (int i = 1; i < s.Length; i++)
        {
            if (s[i] != s[runStart])
            {
                runStart = i;
                continue;
            }
            int runLen = i - runStart + 1;
            if (runLen > threshold)
            {
                char c = s[runStart];
                if (c != ' ' && c != '\t' && c != '\n' && c != '\r') return runStart;
            }
        }
        return -1;
    }

    /// <summary>
    /// Pre-insertion firewall: if the suggestion contains a degenerate run
    /// (5+ identical non-whitespace chars) anywhere, truncate at the start of
    /// the run. If after trimming nothing useful is left, return empty so
    /// AcceptAsync skips insertion entirely.
    /// </summary>
    private static string StripDegenerateTail(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        int idx = FindInteriorRunStart(s, 4);
        if (idx >= 0) s = s[..idx];
        return s.TrimEnd();
    }

    private void Post(Action action)
    {
        if (SynchronizationContext.Current == _ui) action();
        else _ui.Post(_ => action(), null);
    }

    private Task PostAsync(Action action)
    {
        if (SynchronizationContext.Current == _ui)
        {
            try { action(); return Task.CompletedTask; }
            catch (Exception ex) { return Task.FromException(ex); }
        }
        var tcs = new TaskCompletionSource();
        _ui.Post(_ =>
        {
            try { action(); tcs.SetResult(); }
            catch (Exception ex) { tcs.SetException(ex); }
        }, null);
        return tcs.Task;
    }

    private void LogFault(Task t)
    {
        if (t.Exception is { } ex) _logger.LogError(ex, "Background task faulted.");
    }
}
