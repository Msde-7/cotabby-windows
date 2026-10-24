using System.Diagnostics;
using System.Windows.Automation;
using System.Windows.Automation.Text;
using Cotabby.Core.Context;
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
    /// <summary>Class of the window that hosts a Chromium renderer's page content.</summary>
    private const string ChromiumRendererClass = "Chrome_RenderWidgetHostHWND";

    /// <summary>Delays after priming a renderer at which focus is read again.</summary>
    private static readonly int[] PrimeRequeryDelaysMs = [120, 300];

    /// <summary>Renderer windows remembered as primed before the set is cleared.</summary>
    private const int MaxPrimedWindows = 64;

    /// <summary>How long the font read for one field is reused.</summary>
    private static readonly TimeSpan StyleRefreshInterval = TimeSpan.FromSeconds(1);

    private readonly ILogger<UiaFocusTracker> _logger;
    private readonly object _gate = new();
    private readonly object _readGate = new();
    private readonly FocusFlickerGate _flicker = new();
    private readonly HashSet<IntPtr> _primedWindows = [];

    private AutomationFocusChangedEventHandler? _handler;
    private FocusedField? _current;
    private bool _running;
    private bool _disposed;

    private string? _styleFieldId;
    private long _styleReadAt;
    private HostFont _style;

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
            _current = Capture();
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
            lock (_readGate) _flicker.Reset();
            _logger.LogInformation("UIA focus tracker stopped.");
        }
    }

    public FocusedField? Refresh()
    {
        var snap = Capture();
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
            var snap = Capture();
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

    /// <summary>
    /// Reads the focused field. A single failed read of the field that was just readable returns
    /// the last snapshot instead, since hosts drop their text or caret for a moment while redrawing.
    /// </summary>
    private FocusedField? Capture()
    {
        TryCaptureFocused(out var snap, out var elementId);
        lock (_readGate)
        {
            var held = _current;
            if (_flicker.Evaluate(snap?.FieldId ?? elementId, snap is not null) == FocusFlickerGate.Decision.Suppress
                && held is not null && held.FieldId == elementId)
            {
                _logger.LogDebug("Focus read failed once on the same field; keeping the last snapshot.");
                return held;
            }
        }
        return snap;
    }

    private bool TryCaptureFocused(out FocusedField? snapshot, out string? elementId)
    {
        snapshot = null;
        elementId = null;
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
            var info = element.Current;
            elementId = ReadFieldId(element, info);
            if (!IsEditableField(element, info))
            {
                PrimeIfDormantRenderer(info);
                return false;
            }
            snapshot = BuildSnapshot(element, info, elementId);
            return snapshot is not null;
        }
        catch (ElementNotAvailableException) { return false; }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Snapshot build failed.");
            return false;
        }
    }

    private static bool IsEditableField(AutomationElement element, AutomationElement.AutomationElementInformation info)
    {
        try
        {
            if (!info.IsEnabled) return false;
            // IsKeyboardFocusable is not checked. The Windows 11 Notepad document control reports
            // false and still accepts keystrokes.

            var ct = info.ControlType;
            bool isComboBox = ct == ControlType.ComboBox;
            if (ct != ControlType.Edit && ct != ControlType.Document && ct != ControlType.Pane && !isComboBox)
            {
                return false;
            }

            // Read-only fields get no suggestions. Not every editor exposes ValuePattern.
            bool hasValue = element.TryGetCurrentPattern(ValuePattern.Pattern, out var raw);
            if (hasValue && raw is ValuePattern vp && vp.Current.IsReadOnly) return false;

            // Without TextPattern or ValuePattern there is no text to read. A combo box needs a
            // writable value or text, which a plain drop-down list does not have.
            bool hasText = element.TryGetCurrentPattern(TextPattern.Pattern, out _);
            return hasText || hasValue;
        }
        catch (Exception) { return false; }
    }

    /// <summary>
    /// Chromium and Electron hosts build their page accessibility tree only once a client asks for
    /// it. Until then focus lands on the bare renderer pane. Asking that window for its UIA root
    /// wakes the tree, and focus is read again once the page has had time to build it.
    /// </summary>
    private void PrimeIfDormantRenderer(AutomationElement.AutomationElementInformation info)
    {
        if (info.ControlType != ControlType.Pane || info.ClassName != ChromiumRendererClass) return;
        var hwnd = new IntPtr(info.NativeWindowHandle);
        if (hwnd == IntPtr.Zero) return;
        if (!BrowserAppDetector.NeedsWebAccessibilityPriming(TryGetProcessName(info.ProcessId))) return;

        lock (_primedWindows)
        {
            if (_primedWindows.Count >= MaxPrimedWindows) _primedWindows.Clear();
            if (!_primedWindows.Add(hwnd)) return;
        }

        try
        {
            NativeMethods.SendMessageTimeout(hwnd, NativeMethods.WM_GETOBJECT, IntPtr.Zero,
                new IntPtr(NativeMethods.UiaRootObjectId), NativeMethods.SMTO_ABORTIFHUNG, 200, out _);
            _ = AutomationElement.FromHandle(hwnd);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Priming the renderer window failed.");
            return;
        }
        _logger.LogInformation("Primed a dormant web accessibility tree in {Process}.", info.ProcessId);
        _ = RequeryAfterPrimeAsync();
    }

    private async Task RequeryAfterPrimeAsync()
    {
        int waited = 0;
        foreach (int delay in PrimeRequeryDelaysMs)
        {
            await Task.Delay(delay - waited).ConfigureAwait(false);
            waited = delay;
            if (!_running) return;
            try
            {
                var snap = Capture();
                if (snap is null) continue;
                if (_current?.FieldId != snap.FieldId)
                {
                    _current = snap;
                    FocusChanged?.Invoke(this, snap);
                }
                return;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Focus read after priming failed.");
                return;
            }
        }
    }

    private FocusedField? BuildSnapshot(
        AutomationElement element, AutomationElement.AutomationElementInformation info, string fieldId)
    {
        int pid = info.ProcessId;
        string procName = TryGetProcessName(pid);
        string label = info.Name ?? string.Empty;
        string helpText = info.HelpText ?? string.Empty;
        bool isSecure = info.IsPassword
            || SecureFieldDetector.IsSecure(label, helpText, info.LocalizedControlType);

        var textPattern = element.TryGetCurrentPattern(TextPattern.Pattern, out var rawText)
            ? rawText as TextPattern
            : null;
        var (text, caretOffset, hasSelection) = ReadTextAndCaret(element, textPattern);
        var caretRect = ReadCaretRect(textPattern);
        var fieldRect = ToScreenRect(info.BoundingRectangle);
        var ct = info.ControlType;
        bool editLike = ct == ControlType.Edit || ct == ControlType.ComboBox;
        bool singleLine = FieldShape.IsSingleLine(editLike, fieldRect, caretRect);
        var sample = textPattern is null || hasSelection ? default : ReadLineSample(textPattern, caretRect);
        var font = textPattern is null ? default : ReadHostFont(fieldId, textPattern, sample.Range);

        return new FocusedField
        {
            ProcessId = pid,
            ProcessName = procName,
            Text = text,
            CaretOffset = caretOffset,
            CaretRect = caretRect.IsEmpty ? fieldRect : caretRect,
            CaretIsEstimated = caretRect.IsEmpty,
            FieldRect = fieldRect,
            IsSingleLine = singleLine,
            IsTextFieldRole = editLike && !IsMultiLineEdit(info, procName, singleLine),
            IsSecure = isSecure,
            HasSelection = hasSelection,
            FieldId = fieldId,
            Label = label,
            HelpText = helpText,
            AutomationId = info.AutomationId ?? string.Empty,
            ClassName = info.ClassName ?? string.Empty,
            WindowTitle = ForegroundWindowTitle(),
            HostFontName = font.Name,
            HostFontSizePt = font.SizePt,
            HostFontWeight = font.Weight,
            HostIsItalic = font.Italic,
            HostForegroundArgb = font.ForegroundArgb,
            CaretLineRect = sample.LineRect,
            SampleText = sample.Text,
            SampleWidthPx = sample.WidthPx,
        };
    }

    /// <summary>
    /// True when an edit control is known to hold several lines: a Win32 edit with the multi-line
    /// style, or a web text box drawn several lines tall. Managed UIA does not expose the ARIA
    /// multi-line flag, so for web text areas the height is the only signal.
    /// </summary>
    private static bool IsMultiLineEdit(AutomationElement.AutomationElementInformation info, string procName, bool singleLine)
    {
        var hwnd = new IntPtr(info.NativeWindowHandle);
        if (hwnd != IntPtr.Zero && (info.ClassName ?? "").Contains("Edit", StringComparison.OrdinalIgnoreCase))
        {
            return (NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_STYLE) & NativeMethods.ES_MULTILINE) != 0;
        }
        return !singleLine && BrowserAppDetector.NeedsWebAccessibilityPriming(procName);
    }

    /// <summary>Text kept before the caret. The request factory sends at most this much prefix.</summary>
    private const int MaxCharsBeforeCaret = 14000;
    private const int MaxCharsAfterCaret = 512;

    /// <summary>Cap of the document read when the host reports no caret.</summary>
    private const int MaxDocumentChars = 16 * 1024;

    /// <summary>A line sample longer than this is not read. Its width says no more than a shorter one.</summary>
    private const int MaxSampleChars = 400;

    private static (string text, int caretOffset, bool hasSelection) ReadTextAndCaret(
        AutomationElement element, TextPattern? textPattern)
    {
        // TextPattern is preferred because it gives the caret as a selection.
        if (textPattern is not null)
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

    private readonly record struct LineSample(TextPatternRange? Range, ScreenRect LineRect, string? Text, double? WidthPx);

    /// <summary>
    /// The caret's visual line and the text on it before the caret, with the width the host
    /// painted that text at. The width is left out when the text spans more than one line box.
    /// </summary>
    private static LineSample ReadLineSample(TextPattern textPattern, ScreenRect caretRect)
    {
        try
        {
            var selections = textPattern.GetSelection();
            if (selections is not { Length: > 0 }) return default;
            var caret = selections[0];

            var line = caret.Clone();
            line.ExpandToEnclosingUnit(TextUnit.Line);
            var lineRect = Union(line.GetBoundingRectangles());

            var toCaret = line.Clone();
            toCaret.MoveEndpointByRange(TextPatternRangeEndpoint.End, caret, TextPatternRangeEndpoint.Start);
            string text = toCaret.GetText(MaxSampleChars + 1);
            if (text.Length == 0) return new LineSample(null, lineRect, "", null);
            if (text.Length > MaxSampleChars || text.Contains('\n') || text.Contains('\r'))
            {
                return new LineSample(toCaret, lineRect, null, null);
            }

            var rects = toCaret.GetBoundingRectangles();
            var union = Union(rects);
            bool oneLine = !union.IsEmpty
                && (caretRect.IsEmpty || Math.Abs(union.Y - caretRect.Y) < Math.Max(caretRect.Height, 1) * 0.5);
            return new LineSample(toCaret, lineRect, text, oneLine ? union.Width : null);
        }
        catch (Exception) { return default; }
    }

    private readonly record struct HostFont(string? Name, double? SizePt, int? Weight, bool? Italic, uint? ForegroundArgb);

    /// <summary>
    /// Font attributes of the text just before the caret. The line sample is read first since it is
    /// already built; only when its attributes are mixed is a one-character range made. A field's
    /// reading is reused for <see cref="StyleRefreshInterval"/>, so typing does not repeat it.
    /// </summary>
    private HostFont ReadHostFont(string fieldId, TextPattern textPattern, TextPatternRange? sample)
    {
        long now = Stopwatch.GetTimestamp();
        lock (_readGate)
        {
            if (_styleFieldId == fieldId && Stopwatch.GetElapsedTime(_styleReadAt, now) < StyleRefreshInterval)
            {
                return _style;
            }
        }

        HostFont font = default;
        try
        {
            // With no text before the caret on its line, the empty caret range carries the
            // formatting new text will get.
            var range = sample;
            if (range is null && textPattern.GetSelection() is { Length: > 0 } selection) range = selection[0];
            if (range is null) return default;
            font = ReadFont(range);
            if (font.Name is null || font.SizePt is null)
            {
                // The sample mixes faces or sizes; the character right before the caret decides.
                var last = range.Clone();
                last.MoveEndpointByRange(TextPatternRangeEndpoint.Start, range, TextPatternRangeEndpoint.End);
                if (last.MoveEndpointByUnit(TextPatternRangeEndpoint.Start, TextUnit.Character, -1) != 0)
                {
                    font = ReadFont(last);
                }
            }
        }
        catch (Exception) { /* nothing reported */ }

        lock (_readGate)
        {
            _styleFieldId = fieldId;
            _styleReadAt = now;
            _style = font;
        }
        return font;
    }

    private static HostFont ReadFont(TextPatternRange range)
    {
        string? name = Attribute(range, TextPattern.FontNameAttribute) as string;
        double? size = Attribute(range, TextPattern.FontSizeAttribute) is double d && d > 0 ? d : null;
        int? weight = Attribute(range, TextPattern.FontWeightAttribute) is int w && w > 0 ? w : null;
        bool? italic = Attribute(range, TextPattern.IsItalicAttribute) is bool b ? b : null;
        // UIA reports a COLORREF, 0x00BBGGRR.
        uint? argb = Attribute(range, TextPattern.ForegroundColorAttribute) is int bgr
            ? 0xFF000000u | ((uint)bgr & 0xFF) << 16 | ((uint)bgr & 0xFF00) | ((uint)bgr >> 16 & 0xFF)
            : null;
        return new HostFont(string.IsNullOrWhiteSpace(name) ? null : name, size, weight, italic, argb);
    }

    /// <summary>An attribute value, or null when the host reports it as mixed or unsupported.</summary>
    private static object? Attribute(TextPatternRange range, AutomationTextAttribute attribute)
    {
        try
        {
            var value = range.GetAttributeValue(attribute);
            return value == TextPattern.MixedAttributeValue || value == AutomationElement.NotSupported ? null : value;
        }
        catch (Exception) { return null; }
    }

    private static ScreenRect Union(System.Windows.Rect[] rects)
    {
        if (rects is not { Length: > 0 }) return ScreenRect.Empty;
        double left = double.MaxValue, top = double.MaxValue, right = double.MinValue, bottom = double.MinValue;
        foreach (var r in rects)
        {
            if (r.IsEmpty) continue;
            left = Math.Min(left, r.Left);
            top = Math.Min(top, r.Top);
            right = Math.Max(right, r.Right);
            bottom = Math.Max(bottom, r.Bottom);
        }
        return right > left ? new ScreenRect(left, top, right - left, bottom - top) : ScreenRect.Empty;
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

    private static ScreenRect ReadCaretRect(TextPattern? textPattern)
    {
        if (textPattern is not null)
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
