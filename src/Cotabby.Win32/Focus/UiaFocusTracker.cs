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
/// Focus events arrive on a thread-pool thread, so consumers that touch UI must switch to their
/// own <see cref="SynchronizationContext"/>. A read against an element can throw at any time,
/// for example when the host process exits, so every read is guarded and falls back to null.
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
    /// When set, <see cref="Current"/> and <see cref="Refresh"/> return this field instead of
    /// asking UI Automation. For tests only.
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

            // Seeded so Current is not null until the first focus change.
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
            // Focus on a field that is not editable ends the session, so no ghost text is left behind.
            if (snap is null)
            {
                _current = null;
                FocusChanged?.Invoke(this, null);
                return;
            }

            // UIA gives no stable element identity, so every focus event counts as a change.
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
            // IsKeyboardFocusable is not checked. The Windows 11 Notepad document control reports
            // false and still accepts keystrokes.

            var ct = info.ControlType;
            if (ct != ControlType.Edit && ct != ControlType.Document && ct != ControlType.Pane)
            {
                return false;
            }

            // Read-only fields get no suggestions. Not every editor exposes ValuePattern.
            if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var raw)
                && raw is ValuePattern vp && vp.Current.IsReadOnly)
            {
                return false;
            }

            // Without TextPattern or ValuePattern there is no text to read.
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
        string label = info.Name ?? string.Empty;
        string helpText = info.HelpText ?? string.Empty;
        bool isSecure = info.IsPassword
            || SecureFieldDetector.IsSecure(label, helpText, info.LocalizedControlType);

        var (text, caretOffset, hasSelection) = ReadTextAndCaret(element);
        var caretRect = ReadCaretRect(element);
        var fieldRect = ToScreenRect(info.BoundingRectangle);

        return new FocusedField
        {
            ProcessId = pid,
            ProcessName = procName,
            Text = text,
            CaretOffset = caretOffset,
            CaretRect = caretRect.IsEmpty ? fieldRect : caretRect,
            CaretIsEstimated = caretRect.IsEmpty,
            FieldRect = fieldRect,
            IsSingleLine = FieldShape.IsSingleLine(info.ControlType == ControlType.Edit, fieldRect, caretRect),
            IsSecure = isSecure,
            HasSelection = hasSelection,
            Label = label,
            HelpText = helpText,
            AutomationId = info.AutomationId ?? string.Empty,
            ClassName = info.ClassName ?? string.Empty,
            WindowTitle = ForegroundWindowTitle(),
        };
    }

    private static (string text, int caretOffset, bool hasSelection) ReadTextAndCaret(AutomationElement element)
    {
        // TextPattern is preferred because it gives the full text and the caret as a selection.
        if (element.TryGetCurrentPattern(TextPattern.Pattern, out var rawText)
            && rawText is TextPattern textPattern)
        {
            try
            {
                // Capped at 16 KB. Only a window around the caret is needed.
                string full = textPattern.DocumentRange.GetText(16 * 1024);
                int caret = 0;
                bool hasSelection = false;
                var sel = textPattern.GetSelection();
                if (sel is { Length: > 0 })
                {
                    var range = sel[0];
                    hasSelection = range.GetText(1).Length > 0;
                    var preCaret = textPattern.DocumentRange.Clone();
                    preCaret.MoveEndpointByRange(
                        TextPatternRangeEndpoint.End, range, TextPatternRangeEndpoint.Start);
                    caret = preCaret.GetText(16 * 1024).Length;
                }
                return (full, caret, hasSelection);
            }
            catch (Exception) { /* fall through */ }
        }

        if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var rawValue)
            && rawValue is ValuePattern vp)
        {
            string v = vp.Current.Value ?? string.Empty;
            return (v, v.Length, false); // ValuePattern has no caret, so assume the end.
        }

        return (string.Empty, 0, false);
    }

    private static ScreenRect ReadCaretRect(AutomationElement element)
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

    private static unsafe string ForegroundWindowTitle()
    {
        const int MaxTitleChars = 256;
        char* buffer = stackalloc char[MaxTitleChars];
        int length = NativeMethods.GetWindowText(NativeMethods.GetForegroundWindow(), buffer, MaxTitleChars);
        return length > 0 ? new string(buffer, 0, length) : string.Empty;
    }

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
