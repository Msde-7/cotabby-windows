using Cotabby.Core.Focus;
using Cotabby.Core.Input;
using Cotabby.Core.Insertion;
using Cotabby.Core.Macros;
using Cotabby.Core.Overlay;
using Microsoft.Extensions.Logging;

namespace Cotabby.App.Macros;

/// <summary>
/// Drives inline slash macros. Typing <c>/5+5</c> previews the result at the caret
/// and the accept key replaces the typed macro with it.
/// </summary>
/// <remarks>
/// Keys arrive on the keyboard hook thread, which Windows cuts off when a callback runs long, so
/// a query is evaluated on the thread pool. Only the accept key needs the result before the
/// callback returns, to decide whether to swallow it. When the evaluation of the latest query
/// has not finished by then, that one query is evaluated in the callback.
/// </remarks>
public sealed class MacroController : IDisposable
{
    private readonly IKeyboardHook _hook;
    private readonly IFocusTracker _focus;
    private readonly ITextInserter _inserter;
    private readonly IOverlayPresenter _preview;
    private readonly ILogger<MacroController> _logger;
    private readonly MacroEngine _engine = MacroEngine.Standard();
    private readonly MacroTriggerStateMachine _machine = new();
    private readonly object _gate = new();
    private MacroResult? _result;
    // The query _result was evaluated for, or null while its evaluation is pending.
    private string? _resultQuery;
    // Bumped whenever the query or capture changes, so a late evaluation is dropped.
    private int _evaluation;
    private bool _enabled = true;
    private int _capturePid;

    private const int CaretSettleMs = 30;

    public MacroController(
        IKeyboardHook hook, IFocusTracker focus, ITextInserter inserter, IOverlayPresenter preview,
        ILogger<MacroController> logger)
    {
        _hook = hook;
        _focus = focus;
        _inserter = inserter;
        _preview = preview;
        _logger = logger;
        _hook.KeyEvent += OnKeyEvent;
        _focus.FocusChanged += OnFocusChanged;
        // The first evaluation of each family builds its tables, which must not happen inside the hook.
        Task.Run(() =>
        {
            foreach (var query in WarmUpQueries) _engine.Evaluate(query);
        });
    }

    private static readonly string[] WarmUpQueries = ["today", "random", "1km->m", "1usd->eur", "1+1"];

    public bool Enabled
    {
        get => _enabled;
        set
        {
            _enabled = value;
            if (!value) Cancel();
        }
    }

    /// <summary>The key that inserts the result in an app. It follows the accept-word shortcut.</summary>
    public Func<string?, KeyBinding> CommitKeyFor { get; set; } = _ => KeyBinding.Tab;

    /// <summary>Whether a macro may open in a field. The host supplies the app-wide rules.</summary>
    public Func<FocusedField, bool> CanCapture { get; set; } = _ => true;

    /// <summary>Whether a macro is being typed, during which suggestions stand down.</summary>
    public bool IsCapturing
    {
        get { lock (_gate) return _machine.IsActive; }
    }

    public void Dispose()
    {
        _hook.KeyEvent -= OnKeyEvent;
        _focus.FocusChanged -= OnFocusChanged;
        Cancel();
    }

    private void OnKeyEvent(object? sender, KeyEventArgs args)
    {
        if (!_enabled) return;
        lock (_gate)
        {
            if (args.Suppress)
            {
                End();
                return;
            }

            var ev = InlineCommitKey.Normalize(args.Event, CommitKeyFor(_focus.Current?.ProcessName));
            if (ev is { IsKeyDown: true, Kind: KeyKind.Tab } && _machine.IsActive && _resultQuery != _machine.Query)
            {
                // Whether to swallow the key cannot wait for the pending evaluation.
                _evaluation++;
                Preview(EvaluateSafely(_machine.Query), _machine.Query);
            }

            var outcome = _machine.Apply(ev, _result is not null);
            args.Suppress = _machine.ConsumesKey;
            switch (outcome)
            {
                case MacroTriggerStateMachine.Outcome.Opened:
                    _capturePid = _focus.Current?.ProcessId ?? 0;
                    if (_focus.Current is not { IsSecure: false } field || !CanCapture(field)) End();
                    break;
                case MacroTriggerStateMachine.Outcome.QueryChanged:
                    EvaluateLater(_machine.Query);
                    break;
                case MacroTriggerStateMachine.Outcome.Commit when _result is { } result:
                    int deleteCount = _machine.QueryAtCommitLength;
                    _evaluation++;
                    Preview(null, null);
                    _ = Task.Run(() => CommitAsync(result, deleteCount));
                    break;
                case MacroTriggerStateMachine.Outcome.Cancel:
                    _evaluation++;
                    Preview(null, null);
                    break;
            }
        }
    }

    /// <summary>Evaluates <paramref name="query"/> on the thread pool and previews it if it is still current.</summary>
    private void EvaluateLater(string query)
    {
        int evaluation = ++_evaluation;
        _result = null;
        _resultQuery = null;
        _ = Task.Run(() =>
        {
            var result = EvaluateSafely(query);
            lock (_gate)
            {
                if (evaluation == _evaluation) Preview(result, query);
            }
        });
    }

    private MacroResult? EvaluateSafely(string query)
    {
        try
        {
            return _engine.Evaluate(query);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Macro evaluation failed.");
            return null;
        }
    }

    private void Preview(MacroResult? result, string? query)
    {
        _result = result;
        _resultQuery = query;
        if (result is null)
        {
            _preview.Hide();
            return;
        }
        // The caret is read off the hook thread, after the host has applied the key.
        _ = Task.Run(async () =>
        {
            await Task.Delay(CaretSettleMs);
            var field = _focus.Refresh();
            lock (_gate)
            {
                if (ReferenceEquals(_result, result) && field is not null)
                {
                    _preview.Show(result.PreviewText, field.CaretRect, OverlayStyle.Popup);
                }
            }
        });
    }

    private async Task CommitAsync(MacroResult result, int deleteCount)
    {
        try
        {
            if (_focus.Refresh() is not { } field) return;
            await _inserter.ReplaceAsync(field, deleteCount, result.InsertionText, CancellationToken.None);
            _logger.LogInformation("Macro committed (replaced {Count} chars).", deleteCount);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Macro commit failed.");
        }
    }

    private void OnFocusChanged(object? sender, FocusedField? field)
    {
        if (field?.ProcessId != _capturePid) Cancel();
    }

    private void Cancel()
    {
        lock (_gate) End();
    }

    private void End()
    {
        _machine.Reset();
        _evaluation++;
        Preview(null, null);
    }
}
