using System.IO;
using System.Windows.Threading;
using Cotabby.Core.Emoji;
using Cotabby.Core.Focus;
using Cotabby.Core.Input;
using Cotabby.Core.Insertion;
using Cotabby.Win32.Insertion;
using Microsoft.Extensions.Logging;

namespace Cotabby.App.Emoji;

/// <summary>
/// Hosts the inline <c>:emoji:</c> picker on Windows. Feeds the global keyboard stream to an
/// <see cref="EmojiPickerSession"/>, shows its matches in the popup and replaces the typed query
/// with the committed glyph.
/// </summary>
/// <remarks>
/// Key events arrive on the hook thread, focus events on a UI Automation thread and the long pause
/// timer on the thread pool, so every session call is serialized by one lock. The hook can only
/// swallow a key synchronously, which is why the session runs inside the hook callback.
/// </remarks>
public sealed class EmojiPickerController : IDisposable
{
    // How long a capture may sit untouched before it cancels itself.
    private static readonly TimeSpan LongPause = TimeSpan.FromSeconds(8);

    // Lets the host apply the backspaces before the glyph arrives.

    private readonly IKeyboardHook _hook;
    private readonly IFocusTracker _focus;
    private readonly ITextInserter _inserter;
    private readonly ILogger<EmojiPickerController> _logger;
    private readonly EmojiUsageStore _usage;
    private readonly EmojiPickerSession _session;
    private readonly Timer _longPauseTimer;
    private readonly object _gate = new();

    /// <summary>Time for the host to apply a key before the caret is read.</summary>
    private const int CaretSettleMs = 30;

    public EmojiPickerController(
        IKeyboardHook hook,
        IFocusTracker focus,
        ITextInserter inserter,
        EmojiPopupWindow popup,
        Dispatcher dispatcher,
        ILogger<EmojiPickerController> logger,
        string? usageFilePath = null)
    {
        _hook = hook;
        _focus = focus;
        _inserter = inserter;
        _logger = logger;
        _usage = new EmojiUsageStore(usageFilePath ?? DefaultUsageFilePath);
        _session = new EmojiPickerSession(
            () => new EmojiMatcher(EmojiCatalog.Bundled),
            new DispatchedPanel(popup, dispatcher),
            focus,
            _usage,
            (field, glyph, fallbackDeleteCount) =>
                Task.Run(() => ReplaceQueryAsync(field, glyph, fallbackDeleteCount)));
        _longPauseTimer = new Timer(_ => CancelCapture());
        // The hook callback has a hard time budget and a cold decode of the catalog takes about
        // 100 ms, so it happens here instead of on the first colon.
        Task.Run(() => EmojiCatalog.Bundled);
        _hook.KeyEvent += OnKeyEvent;
        _focus.FocusChanged += OnFocusChanged;
    }

    /// <summary><c>%APPDATA%\Cotabby\emoji-usage.json</c>.</summary>
    public static string DefaultUsageFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Cotabby", "emoji-usage.json");

    public bool Enabled
    {
        get => _session.Enabled;
        set => _session.Enabled = value;
    }

    /// <summary>The key that commits the selected emoji. Follows the accept-word shortcut.</summary>
    public KeyBinding CommitKey { get; set; } = KeyBinding.Tab;

    public EmojiSkinTone PreferredSkinTone
    {
        get => _session.PreferredSkinTone;
        set => _session.PreferredSkinTone = value;
    }

    public EmojiGender PreferredGender
    {
        get => _session.PreferredGender;
        set => _session.PreferredGender = value;
    }

    /// <summary>Whether a capture is open, during which Tab and Escape belong to the picker.</summary>
    public bool IsCapturing
    {
        get { lock (_gate) return _session.IsCapturing; }
    }

    /// <summary>Forgets recently and frequently used emoji so the picker ranks from scratch.</summary>
    public void ClearHistory()
    {
        lock (_gate) _usage.Clear();
    }

    public void Dispose()
    {
        _hook.KeyEvent -= OnKeyEvent;
        _focus.FocusChanged -= OnFocusChanged;
        _longPauseTimer.Dispose();
        CancelCapture();
    }

    private void OnKeyEvent(object? sender, KeyEventArgs args)
    {
        lock (_gate)
        {
            if (args.Suppress)
            {
                // An earlier handler took this key, so the text before the caret is changing.
                _session.Handle(EmojiTriggerInput.DismissExternally);
            }
            else
            {
                args.Suppress = _session.HandleKey(InlineCommitKey.Normalize(args.Event, CommitKey));
            }
            _longPauseTimer.Change(_session.IsCapturing ? LongPause : Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            if (_session.IsCapturing) _ = Task.Run(FollowCaretAsync);
        }
    }

    private async Task FollowCaretAsync()
    {
        await Task.Delay(CaretSettleMs);
        if (_focus.Refresh() is not { } field) return;
        lock (_gate) _session.UpdateCaret(field);
    }

    private void OnFocusChanged(object? sender, FocusedField? field)
    {
        lock (_gate) _session.HandleFocusChanged(field);
    }

    private void CancelCapture()
    {
        lock (_gate) _session.Cancel();
    }

    /// <summary>
    /// Deletes the typed <c>:query</c> run and inserts the glyph. Runs on the thread pool so the
    /// synthetic keys are never posted from inside the keystroke's own hook callback.
    /// </summary>
    private async Task ReplaceQueryAsync(FocusedField captureField, string glyph, int fallbackDeleteCount)
    {
        try
        {
            var field = _focus.Refresh() ?? captureField;
            int deleteCount = EmojiQueryRun.DeleteCount(field, fallbackDeleteCount);
            await _inserter.ReplaceAsync(field, deleteCount, glyph, CancellationToken.None);
            _logger.LogInformation("Emoji committed {Glyph} (deleted {Count} chars)", glyph, deleteCount);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Emoji commit failed.");
        }
    }

    /// <summary>Marshals the session's panel calls onto the popup's UI thread.</summary>
    private sealed class DispatchedPanel(EmojiPopupWindow popup, Dispatcher dispatcher) : IEmojiPickerPanel
    {
        public void Show(string query, IReadOnlyList<EmojiMatch> matches, int selectedIndex, ScreenRect caretRect) =>
            dispatcher.BeginInvoke(() => popup.ShowMatches(query, matches, selectedIndex, caretRect));

        public void SetSelectedIndex(int index) => dispatcher.BeginInvoke(() => popup.SetSelectedIndex(index));

        public void Hide() => dispatcher.BeginInvoke(() => popup.Hide());
    }
}
