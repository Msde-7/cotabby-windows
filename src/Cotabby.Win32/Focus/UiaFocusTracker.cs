using System.Diagnostics;
using System.Windows.Automation;
using System.Windows.Automation.Text;
using Cotabby.Core.Focus;
using Cotabby.Win32.Interop;
using Microsoft.Extensions.Logging;

namespace Cotabby.Win32.Focus;

/// <summary>
/// UI Automation implementation of <see cref="IFocusTracker"/>.
/// </summary>
/// <remarks>
/// We subscribe to <see cref="Automation.AddAutomationFocusChangedEventHandler"/>
/// rather than polling like the macOS port. UIA event delivery happens on a
/// background pool thread, so consumers wanting to touch UI must hop through
/// their own <see cref="SynchronizationContext"/>.
///
/// Reads against an element can throw at any time (the host process exits,
/// COM tear-down races, RPC server unavailable). Every read is wrapped in a
/// best-effort try/catch and falls back to <c>null</c>.
/// </remarks>
public sealed class UiaFocusTracker : IFocusTracker
{
    private readonly ILogger<UiaFocusTracker> _logger;
    private readonly object _gate = new();

    private AutomationFocusChangedEventHandler? _handler;
    private FocusedField? _current;
    private bool _running;
    private bool _disposed;

    public UiaFocusTracker(ILogger<UiaFocusTracker> logger) => _logger = logger;

    public FocusedField? Current => _fakeField ?? _current;

    public event EventHandler<FocusedField?>? FocusChanged;

    private FocusedField? _fakeField;

    /// <summary>
    /// Test backdoor: when non-null, <see cref="Current"/> and <see cref="Refresh"/>
    /// return this synthetic field instead of consulting UIA. Used by the App's
    /// self-test to exercise the coordinator without depending on real Windows
    /// focus behavior. Never set from production code paths.
    /// </summary>
    public void SetFakeFieldForTesting(FocusedField? fake)
    {
        _fakeField = fake;
        FocusChanged?.Invoke(this, fake);
    }

    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_running) return;
            _running = true;

            _handler = OnFocusChanged;
            Automation.AddAutomationFocusChangedEventHandler(_handler);
            _logger.LogInformation("UIA focus tracker started.");

            // Seed the current snapshot so consumers calling Current right after
            // Start don't see null until the first focus change.
            TryCaptureFocused(out var initial);
            _current = initial;
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (!_running) return;
            _running = false;
            if (_handler is not null)
            {
                try { Automation.RemoveAutomationFocusChangedEventHandler(_handler); }
                catch (Exception ex) { _logger.LogWarning(ex, "RemoveAutomationFocusChangedEventHandler threw."); }
                _handler = null;
            }
            _current = null;
            _logger.LogInformation("UIA focus tracker stopped.");
        }
    }

    public FocusedField? Refresh()
    {
        if (_fakeField is not null) return _fakeField;
        TryCaptureFocused(out var snap);
        _current = snap;
        return snap;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }

    private void OnFocusChanged(object? sender, AutomationFocusChangedEventArgs e)
    {
        if (!_running) return;

        try
        {
            TryCaptureFocused(out var snap);
            // Treat focus to non-editable as "left supported field" so the
            // coordinator tears down its session and we don't leak ghost text.
            if (snap is null)
            {
                _current = null;
                FocusChanged?.Invoke(this, null);
                return;
            }

            // Identity changed if pid changed or element bounds shifted significantly.
            // We don't have a stable element identity through ElementHandle since
            // UIA's RuntimeId is a managed int[] and equality isn't trivial across
            // events. Treat every focus event as a state change — cheap enough.
            _current = snap;
            FocusChanged?.Invoke(this, snap);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Focus change handling failed.");
        }
    }

    private bool TryCaptureFocused(out FocusedField? snapshot)
    {
        snapshot = null;
        AutomationElement? element;
        try
        {
            element = AutomationElement.FocusedElement;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "AutomationElement.FocusedElement threw.");
            return false;
        }
        if (element is null) return false;

        try
        {
            if (!IsEditableField(element)) return false;
            snapshot = BuildSnapshot(element);
            return snapshot is not null;
        }
        catch (ElementNotAvailableException) { return false; }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Snapshot build failed.");
            return false;
        }
    }

    private static bool IsEditableField(AutomationElement element)
    {
        try
        {
            var info = element.Current;
            if (!info.IsEnabled) return false;
            // IsKeyboardFocusable is unreliable — Win11 Notepad's RichEditBox
            // exposes false for the document control even though it accepts
            // keystrokes — so we don't gate on it. We rely on the control type
            // and a TextPattern / ValuePattern probe instead.

            var ct = info.ControlType;
            if (ct != ControlType.Edit && ct != ControlType.Document && ct != ControlType.Pane)
            {
                return false;
            }

            // Read-only fields (URL bars in read mode, disabled inputs) shouldn't
            // get suggestions. Best-effort — not every editor exposes ValuePattern.
            if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var raw)
                && raw is ValuePattern vp && vp.Current.IsReadOnly)
            {
                return false;
            }

            // Require at least one of TextPattern or ValuePattern so we know we
            // can actually read text out of this element. Without that the
            // request would have an empty prefix.
            bool hasText = element.TryGetCurrentPattern(TextPattern.Pattern, out _);
            bool hasValue = element.TryGetCurrentPattern(ValuePattern.Pattern, out _);
            return hasText || hasValue;
        }
        catch (Exception) { return false; }
    }

    private FocusedField? BuildSnapshot(AutomationElement element)
    {
        var info = element.Current;

        int pid = info.ProcessId;
        string procName = TryGetProcessName(pid);
        bool isPassword = info.IsPassword;
        bool isSingleLine = info.ControlType == ControlType.Edit;

        var (text, caretOffset) = ReadTextAndCaret(element);
        var caretRect = ReadCaretRect(element);
        var fieldRect = ToScreenRect(info.BoundingRectangle);

        return new FocusedField
        {
            ElementHandle = element,
            ProcessId = pid,
            ProcessName = procName,
            Text = text,
            CaretOffset = caretOffset,
            CaretRect = caretRect.IsEmpty ? fieldRect : caretRect,
            FieldRect = fieldRect,
            IsSingleLine = isSingleLine,
            IsSecure = isPassword,
        };
    }

    private (string text, int caretOffset) ReadTextAndCaret(AutomationElement element)
    {
        // Prefer TextPattern: it gives both full text and the caret-as-selection.
        if (element.TryGetCurrentPattern(TextPattern.Pattern, out var rawText)
            && rawText is TextPattern textPattern)
        {
            try
            {
                // Cap to 16KB — coordinators only need a window around the caret.
                string full = textPattern.DocumentRange.GetText(16 * 1024);
                int caret = 0;
                var sel = textPattern.GetSelection();
                if (sel is { Length: > 0 })
                {
                    var range = sel[0];
                    var preCaret = textPattern.DocumentRange.Clone();
                    preCaret.MoveEndpointByRange(
                        TextPatternRangeEndpoint.End, range, TextPatternRangeEndpoint.Start);
                    caret = preCaret.GetText(16 * 1024).Length;
                }
                return (full, caret);
            }
            catch (Exception) { /* fall through */ }
        }

        if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var rawValue)
            && rawValue is ValuePattern vp)
        {
            string v = vp.Current.Value ?? string.Empty;
            return (v, v.Length); // caret at end as a fallback
        }

        return (string.Empty, 0);
    }

    private ScreenRect ReadCaretRect(AutomationElement element)
    {
        if (element.TryGetCurrentPattern(TextPattern.Pattern, out var raw)
            && raw is TextPattern textPattern)
        {
            try
            {
                var sel = textPattern.GetSelection();
                if (sel is { Length: > 0 })
                {
                    var rects = sel[0].GetBoundingRectangles();
                    if (rects.Length > 0)
                    {
                        return ToScreenRect(rects[^1]);
                    }
                }
            }
            catch (Exception) { /* fall through */ }
        }
        return ScreenRect.Empty;
    }

    private static ScreenRect ToScreenRect(System.Windows.Rect r) =>
        new(r.X, r.Y, r.Width, r.Height);

    private static string TryGetProcessName(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.ProcessName;
        }
        catch (Exception) { return string.Empty; }
    }
}
