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
    private readonly CaptureOrder<FocusedField> _current = new();
    private readonly PrecedingTextReads _precedingReads = new();

    private AutomationFocusChangedEventHandler? _handler;
    private bool _running;
    private bool _disposed;

    private string? _styleFieldId;
    private long _styleReadAt;
    private HostFont _style;

    public UiaFocusTracker(ILogger<UiaFocusTracker> logger) => _logger = logger;

    public FocusedField? Current => _current.Current;

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
            long sequence = _current.Begin();
            _current.TryPublish(sequence, Capture());
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
            _current.Reset();
            lock (_readGate) _flicker.Reset();
            _logger.LogInformation("UIA focus tracker stopped.");
        }
    }

    public FocusedField? Refresh(bool readHostText = false)
    {
        long sequence = _current.Begin();
        var snap = Capture(readHostText);
        _current.TryPublish(sequence, snap);
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
            long sequence = _current.Begin();
            var snap = Capture();
            // When a capture that started later finished first, this one describes focus as it was
            // before, so the later one is passed on instead.
            if (!_current.TryPublish(sequence, snap)) snap = _current.Current;

            // Focus on a field that is not editable ends the session, so no ghost text is left behind.
            // Every other focus event is passed on. Listeners compare FieldId to tell a new field.
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
    private FocusedField? Capture(bool readHostText = false)
    {
        TryCaptureFocused(readHostText, out var snap, out var elementId);
        lock (_readGate)
        {
            var held = _current.Current;
            if (_flicker.Evaluate(snap?.FieldId ?? elementId, snap is not null) == FocusFlickerGate.Decision.Suppress
                && held is not null && held.FieldId == elementId)
            {
                _logger.LogDebug("Focus read failed once on the same field; keeping the last snapshot.");
                return held;
            }
        }
        return snap;
    }

    private bool TryCaptureFocused(bool readHostText, out FocusedField? snapshot, out string? elementId)
    {
        snapshot = null;
        elementId = null;
        if (FocusedElement() is not { } focused) return false;
        var (element, cached) = focused;

        try
        {
            var info = cached ? element.Cached : element.Current;
            elementId = ReadFieldId(element, info);
            if (!IsEditableField(element, info, cached))
            {
                PrimeIfDormantRenderer(info);
                return false;
            }
            snapshot = BuildSnapshot(element, info, cached, elementId, readHostText);
            return snapshot is not null;
        }
        catch (ElementNotAvailableException) { return false; }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Snapshot build failed.");
            return false;
        }
    }

    /// <summary>
    /// The focused element with every property and pattern a capture reads fetched in the same
    /// cross-process call. Reading them one by one costs a round trip each. When the batched
    /// fetch fails, the element is fetched plain and read live, as before.
    /// </summary>
    private (AutomationElement Element, bool Cached)? FocusedElement()
    {
        try
        {
            using (FocusCache.Activate())
            {
                return AutomationElement.FocusedElement is { } element ? (element, true) : null;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Fetching the focused element with its properties threw.");
        }

        try
        {
            return AutomationElement.FocusedElement is { } element ? (element, false) : null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "AutomationElement.FocusedElement threw.");
            return null;
        }
    }

    // CacheRequest.Activate pushes onto a per-thread stack, so each thread keeps its own request.
    [ThreadStatic] private static CacheRequest? _focusCache;

    private static CacheRequest FocusCache => _focusCache ??= BuildFocusCache();

    private static CacheRequest BuildFocusCache()
    {
        var request = new CacheRequest { TreeScope = TreeScope.Element, AutomationElementMode = AutomationElementMode.Full };
        AutomationProperty[] properties =
        [
            AutomationElement.IsEnabledProperty, AutomationElement.ControlTypeProperty,
            AutomationElement.ProcessIdProperty, AutomationElement.NameProperty, AutomationElement.HelpTextProperty,
            AutomationElement.IsPasswordProperty, AutomationElement.LocalizedControlTypeProperty,
            AutomationElement.BoundingRectangleProperty, AutomationElement.NativeWindowHandleProperty,
            AutomationElement.ClassNameProperty, AutomationElement.AutomationIdProperty, ValuePattern.IsReadOnlyProperty,
        ];
        foreach (var property in properties) request.Add(property);
        request.Add(ValuePattern.Pattern);
        request.Add(TextPattern.Pattern);
        return request;
    }

    private static bool TryGetPattern(AutomationElement element, AutomationPattern pattern, bool cached, out object? value) =>
        cached ? element.TryGetCachedPattern(pattern, out value) : element.TryGetCurrentPattern(pattern, out value);

    private static bool IsEditableField(
        AutomationElement element, AutomationElement.AutomationElementInformation info, bool cached)
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
            bool hasValue = TryGetPattern(element, ValuePattern.Pattern, cached, out var raw);
            if (hasValue && raw is ValuePattern vp && (cached ? vp.Cached : vp.Current).IsReadOnly) return false;

            // Without TextPattern or ValuePattern there is no text to read. A combo box needs a
            // writable value or text, which a plain drop-down list does not have.
            bool hasText = TryGetPattern(element, TextPattern.Pattern, cached, out _);
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
        if (!BrowserAppDetector.NeedsWebAccessibilityPriming(ProcessNames.For(info.ProcessId).Process)) return;

        lock (_primedWindows)
        {
            if (_primedWindows.Count >= MaxPrimedWindows) _primedWindows.Clear();
            if (!_primedWindows.Add(hwnd)) return;
        }

        try
        {
            NativeMethods.SendMessageTimeout(hwnd, NativeMethods.WM_GETOBJECT, IntPtr.Zero,
                new IntPtr(NativeMethods.UiaRootObjectId), NativeMethods.SMTO_ABORTIFHUNG, 200, out _);
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
                long sequence = _current.Begin();
                var snap = Capture();
                if (snap is null) continue;
                if (_current.Current?.FieldId != snap.FieldId && _current.TryPublish(sequence, snap))
                {
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
        AutomationElement element, AutomationElement.AutomationElementInformation info, bool cached, string fieldId,
        bool readHostText)
    {
        int pid = info.ProcessId;
        var names = ProcessNames.For(pid);
        string label = info.Name ?? string.Empty;
        string helpText = info.HelpText ?? string.Empty;
        bool isSecure = info.IsPassword
            || SecureFieldDetector.IsSecure(label, helpText, info.LocalizedControlType);

        var textPattern = TryGetPattern(element, TextPattern.Pattern, cached, out var rawText)
            ? rawText as TextPattern
            : null;
        var caret = ReadCaretRange(textPattern);
        // A secure field's text is never read, whatever marked it secure. Its caret still places the overlay.
        var (text, caretOffset, hasSelection) = isSecure
            ? (string.Empty, 0, false)
            : ReadTextAndCaret(element, cached, textPattern, caret, fieldId);
        var caretRect = ReadCaretRect(caret);
        var fieldRect = ToScreenRect(info.BoundingRectangle);
        var ct = info.ControlType;
        bool editLike = ct == ControlType.Edit || ct == ControlType.ComboBox;
        bool singleLine = FieldShape.IsSingleLine(editLike, fieldRect, caretRect);
        // The font and line sample cost several more round trips, so only the snapshot a suggestion
        // is drawn from reads them.
        var styleCaret = readHostText && !hasSelection && !isSecure ? caret : null;
        var sample = styleCaret is null ? default : ReadLineSample(styleCaret, caretRect, fieldRect);
        var font = styleCaret is null ? default : ReadHostFont(fieldId, styleCaret, sample.Range);

        return SmartComposePolicy.Apply(new FocusedField
        {
            ProcessId = pid,
            ProcessName = names.Process,
            Text = text,
            CaretOffset = caretOffset,
            CaretRect = caretRect.IsEmpty ? fieldRect : caretRect,
            CaretIsEstimated = caretRect.IsEmpty,
            FieldRect = fieldRect,
            IsSingleLine = singleLine,
            IsTextFieldRole = editLike && !IsMultiLineEdit(info, singleLine, text),
            IsSecure = isSecure,
            HasSelection = hasSelection,
            FieldId = fieldId,
            Label = label,
            HelpText = helpText,
            AutomationId = info.AutomationId ?? string.Empty,
            ClassName = info.ClassName ?? string.Empty,
            WindowTitle = NativeMethods.WindowTitle(NativeMethods.GetForegroundWindow()),
            AppDisplayName = names.Display,
            ImeLanguageId = ImeState.ConvertingLanguage(),
            HostFontName = font.Name,
            HostFontSizePt = font.SizePt,
            HostFontWeight = font.Weight,
            HostIsItalic = font.Italic,
            CaretLineRect = sample.LineRect,
            SampleText = sample.Text,
            SampleWidthPx = sample.WidthPx,
        });
    }

    /// <summary>
    /// True when an edit control holds several lines. A Win32 edit says so in its style. Managed UIA
    /// exposes no multi-line flag for web, WPF or WinUI text boxes, so those go by their text and height.
    /// </summary>
    private static bool IsMultiLineEdit(AutomationElement.AutomationElementInformation info, bool singleLine, string text)
    {
        var hwnd = new IntPtr(info.NativeWindowHandle);
        if (hwnd != IntPtr.Zero && (info.ClassName ?? "").Contains("Edit", StringComparison.OrdinalIgnoreCase))
        {
            return (NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_STYLE) & NativeMethods.ES_MULTILINE) != 0;
        }
        return FieldShape.HoldsSeveralLines(singleLine, text);
    }

    private const int MaxCharsAfterCaret = 512;

    /// <summary>
    /// Paragraphs a bounded read of a long document reaches back from the caret. Even short lines
    /// of code add up to more than the text kept.
    /// </summary>
    private const int BoundedReadParagraphs = 1000;

    /// <summary>Cap of the document read when the host reports no caret.</summary>
    private const int MaxDocumentChars = 16 * 1024;

    /// <summary>A line sample longer than this is not read. Its width says no more than a shorter one.</summary>
    private const int MaxSampleChars = 400;

    /// <summary>The first selection, which is the caret when nothing is selected.</summary>
    private static TextPatternRange? ReadCaretRange(TextPattern? textPattern)
    {
        try
        {
            return textPattern?.GetSelection() is { Length: > 0 } selections ? selections[0] : null;
        }
        catch (Exception) { return null; }
    }

    private (string text, int caretOffset, bool hasSelection) ReadTextAndCaret(
        AutomationElement element, bool cached, TextPattern? textPattern, TextPatternRange? selection, string fieldId)
    {
        // TextPattern is preferred because it gives the caret as a selection.
        if (textPattern is not null)
        {
            try
            {
                if (selection is null)
                {
                    return (textPattern.DocumentRange.GetText(MaxDocumentChars), 0, false);
                }

                bool hasSelection = selection.GetText(1).Length > 0;
                var (before, after) = ReadAroundSelection(textPattern.DocumentRange, selection, fieldId);
                return (before + after, before.Length, hasSelection);
            }
            catch (Exception) { /* fall through */ }
        }

        if (TryGetPattern(element, ValuePattern.Pattern, cached, out var rawValue)
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
    /// seconds in a WPF text box, and reading up to the caret takes milliseconds. A long document
    /// may instead be read from a range moved back by paragraph, as <see cref="PrecedingTextReads"/>
    /// decides.
    /// </remarks>
    private (string before, string after) ReadAroundSelection(
        TextPatternRange document, TextPatternRange selection, string fieldId)
    {
        string? before = _precedingReads.UseBounded(fieldId) ? ReadBoundedBeforeCaret(document, selection, fieldId) : null;
        if (before is null)
        {
            long started = Stopwatch.GetTimestamp();
            var toCaret = document.Clone();
            toCaret.MoveEndpointByRange(TextPatternRangeEndpoint.End, selection, TextPatternRangeEndpoint.Start);
            string all = toCaret.GetText(-1);
            _precedingReads.FullRead(fieldId, all.Length, Stopwatch.GetElapsedTime(started));
            before = PrecedingTextReads.KeepEnd(all);
        }

        var fromCaret = document.Clone();
        fromCaret.MoveEndpointByRange(TextPatternRangeEndpoint.Start, selection, TextPatternRangeEndpoint.End);
        string after = FieldReads.WithoutBrokenEnd(fromCaret.GetText(MaxCharsAfterCaret));

        return (before, after);
    }

    /// <summary>
    /// The text before the caret read from a range that starts a bounded number of paragraphs
    /// back, or null when that range did not reach far enough and a full read is needed.
    /// </summary>
    private string? ReadBoundedBeforeCaret(TextPatternRange document, TextPatternRange selection, string fieldId)
    {
        long started = Stopwatch.GetTimestamp();
        string? before = null;
        try
        {
            var toCaret = selection.Clone();
            toCaret.MoveEndpointByRange(TextPatternRangeEndpoint.End, selection, TextPatternRangeEndpoint.Start);
            toCaret.MoveEndpointByUnit(TextPatternRangeEndpoint.Start, TextUnit.Paragraph, -BoundedReadParagraphs);
            string text = toCaret.GetText(-1);
            before = PrecedingTextReads.FromBounded(
                text,
                text.Length <= PrecedingTextReads.Kept
                    && toCaret.CompareEndpoints(TextPatternRangeEndpoint.Start, document, TextPatternRangeEndpoint.Start) == 0);
        }
        catch (Exception)
        {
            // A provider that cannot move by paragraph is read in full.
        }
        _precedingReads.BoundedRead(fieldId, before is not null, Stopwatch.GetElapsedTime(started));
        return before;
    }

    private readonly record struct LineSample(TextPatternRange? Range, ScreenRect LineRect, string? Text, double? WidthPx);

    /// <summary>
    /// The caret's visual line and the text on it before the caret, with the width the host
    /// painted that text at. The width is left out when the text spans more than one line box, and
    /// a line box outside the field is some other line's.
    /// </summary>
    private static LineSample ReadLineSample(TextPatternRange caret, ScreenRect caretRect, ScreenRect fieldRect)
    {
        try
        {
            var line = caret.Clone();
            line.ExpandToEnclosingUnit(TextUnit.Line);
            var lineRect = Union(line.GetBoundingRectangles());
            if (!fieldRect.IsEmpty && !FieldReads.Overlaps(lineRect, fieldRect)) lineRect = ScreenRect.Empty;

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
            bool oneLine = FieldReads.IsOnCaretLine(union, caretRect);
            return new LineSample(toCaret, lineRect, text, oneLine ? union.Width : null);
        }
        catch (Exception) { return default; }
    }

    private readonly record struct HostFont(string? Name, double? SizePt, int? Weight, bool? Italic);

    /// <summary>
    /// Font attributes at the caret, whose empty range carries the formatting new text will get.
    /// Hosts that report nothing for an empty range are asked about the line sample instead. No
    /// range is moved by character, which takes seconds in a WPF text box. A field's reading is
    /// reused for <see cref="StyleRefreshInterval"/>.
    /// </summary>
    private HostFont ReadHostFont(string fieldId, TextPatternRange caret, TextPatternRange? sample)
    {
        long now = Stopwatch.GetTimestamp();
        lock (_readGate)
        {
            if (_styleFieldId == fieldId && Stopwatch.GetElapsedTime(_styleReadAt, now) < StyleRefreshInterval)
            {
                return _style;
            }
        }

        var font = ReadFont(caret);
        if ((font.Name is null || font.SizePt is null) && sample is not null)
        {
            var fromSample = ReadFont(sample);
            font = new HostFont(
                font.Name ?? fromSample.Name, font.SizePt ?? fromSample.SizePt,
                font.Weight ?? fromSample.Weight, font.Italic ?? fromSample.Italic);
        }

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
        return new HostFont(string.IsNullOrWhiteSpace(name) ? null : name, size, weight, italic);
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

    /// <summary>The union of the rects a host reported. Only Rect.Empty, which has no position, is left out.</summary>
    private static ScreenRect Union(System.Windows.Rect[] rects)
    {
        if (rects is not { Length: > 0 }) return ScreenRect.Empty;
        Span<ScreenRect> converted = rects.Length <= 64 ? stackalloc ScreenRect[rects.Length] : new ScreenRect[rects.Length];
        int count = 0;
        foreach (var r in rects)
        {
            if (!r.IsEmpty) converted[count++] = ToScreenRect(r);
        }
        return FieldReads.Union(converted[..count]);
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

    private static ScreenRect ReadCaretRect(TextPatternRange? caret)
    {
        try
        {
            if (caret?.GetBoundingRectangles() is { Length: > 0 } rects) return ToScreenRect(rects[^1]);
        }
        catch (Exception) { /* fall through */ }
        return ScreenRect.Empty;
    }

    private static ScreenRect ToScreenRect(System.Windows.Rect r) =>
        new(r.X, r.Y, r.Width, r.Height);

}
