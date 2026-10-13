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

    public FocusedField? Current => _current;

    public event EventHandler<FocusedField?>? FocusChanged;

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

            // Every focus event is passed on. Listeners compare FieldId to tell a new field.
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
            FieldId = ReadFieldId(element, info),
            Label = label,
            HelpText = helpText,
            AutomationId = info.AutomationId ?? string.Empty,
            ClassName = info.ClassName ?? string.Empty,
            WindowTitle = ForegroundWindowTitle(),
        };
    }

    /// <summary>Text kept before the caret. The request factory sends at most this much prefix.</summary>
    private const int MaxCharsBeforeCaret = 14000;
    private const int MaxCharsAfterCaret = 512;

    /// <summary>Cap of the document read when the host reports no caret.</summary>
    private const int MaxDocumentChars = 16 * 1024;

    private static (string text, int caretOffset, bool hasSelection) ReadTextAndCaret(AutomationElement element)
    {
        // TextPattern is preferred because it gives the caret as a selection.
        if (element.TryGetCurrentPattern(TextPattern.Pattern, out var rawText)
            && rawText is TextPattern textPattern)
        {
            try
            {
                var selections = textPattern.GetSelection();
                if (selections is not { Length: > 0 })
                {
                    return (textPattern.DocumentRange.GetText(MaxDocumentChars), 0, false);
                }

                var selection = selections[0];
                bool hasSelection = selection.GetText(1).Length > 0;
                var (before, after) = ReadAroundSelection(textPattern.DocumentRange, selection);
                return (before + after, before.Length, hasSelection);
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

    /// <summary>
    /// The text before the start of the selection and the text after its end, each cut to a
    /// window next to the caret.
    /// </summary>
    /// <remarks>
    /// The ranges are built with MoveEndpointByRange only. MoveEndpointByUnit by character takes
    /// seconds in a WPF text box, and reading up to the caret takes milliseconds.
    /// </remarks>
    private static (string before, string after) ReadAroundSelection(TextPatternRange document, TextPatternRange selection)
    {
        var toCaret = document.Clone();
        toCaret.MoveEndpointByRange(TextPatternRangeEndpoint.End, selection, TextPatternRangeEndpoint.Start);
        string before = toCaret.GetText(-1);
        if (before.Length > MaxCharsBeforeCaret)
        {
            before = before[^MaxCharsBeforeCaret..];
            // The cut must not leave half a surrogate pair.
            if (char.IsLowSurrogate(before[0])) before = before[1..];
        }

        var fromCaret = document.Clone();
        fromCaret.MoveEndpointByRange(TextPatternRangeEndpoint.Start, selection, TextPatternRangeEndpoint.End);
        string after = fromCaret.GetText(MaxCharsAfterCaret);
        if (after.Length > 0 && char.IsHighSurrogate(after[^1])) after = after[..^1];

        return (before, after);
    }

    private static string ReadFieldId(AutomationElement element, AutomationElement.AutomationElementInformation info)
    {
        int[]? runtimeId = null;
        try { runtimeId = element.GetRuntimeId(); }
        catch (Exception) { /* fall through */ }

        return runtimeId is { Length: > 0 }
            ? string.Join('.', runtimeId)
            : $"{info.AutomationId}|{info.ControlType.ProgrammaticName}";
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
