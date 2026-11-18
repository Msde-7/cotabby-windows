using System.IO;
using System.Windows.Threading;
using Cotabby.App.Inline;
using Cotabby.Core.Emoji;
using Cotabby.Core.Focus;
using Cotabby.Core.Input;
using Cotabby.Core.Insertion;
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
public sealed class EmojiPickerController : InlineCaptureController
{
    // How long a capture may sit untouched before it cancels itself.
    private static readonly TimeSpan LongPause = TimeSpan.FromSeconds(8);

    private readonly EmojiUsageStore _usage;
    private readonly EmojiPickerSession _session;
    private readonly Timer _longPauseTimer;
    private readonly object _gate = new();

    public EmojiPickerController(
        IKeyboardHook hook,
        IFocusTracker focus,
        ITextInserter inserter,
        EmojiPopupWindow popup,
        Dispatcher dispatcher,
        ILogger<EmojiPickerController> logger)
        : base(hook, focus, inserter, logger)
    {
        // Commits are recorded inside the keyboard hook, which must never wait on the disk.
        _usage = new EmojiUsageStore(UsageFilePath, write => RunInBackground("Saving emoji usage", write));
        _session = new EmojiPickerSession(
            () => new EmojiMatcher(EmojiCatalog.Bundled),
            new DispatchedPanel(popup, dispatcher),
            focus,
            _usage,
            (field, glyph, typedRun) =>
                RunInBackground("Emoji commit", () => ReplaceQueryAsync(field, glyph, typedRun)));
        _longPauseTimer = new Timer(_ => CancelCapture());
        // The hook callback has a hard time budget and a cold decode of the catalog takes about
        // 100 ms, so it happens here instead of on the first colon.
        Task.Run(WarmUp);
        Subscribe();
    }

    /// <summary>
    /// Does once, off the hook thread, the first-time work a capture and a commit would otherwise
    /// do inside the hook: decoding the catalog, the first match and building the JSON
    /// serializer that a commit uses to write the usage file. The throwaway store reads a file
    /// that does not exist and discards its write.
    /// </summary>
    private void WarmUp()
    {
        try
        {
            new EmojiMatcher(EmojiCatalog.Bundled).Matches("smile", EmojiUsageSnapshot.Empty);
            var scratch = Path.Combine(Path.GetTempPath(), $"cotabby-warmup-{Guid.NewGuid():N}", "emoji-usage.json");
            new EmojiUsageStore(scratch, _ => { }).Record("smile");
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Emoji picker warm-up failed.");
        }
    }

    private static string UsageFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Cotabby", "emoji-usage.json");

    public bool Enabled
    {
        get => _session.Enabled;
        set => _session.Enabled = value;
    }

    public Func<FocusedField, bool> CanCapture
    {
        get => _session.CanCapture;
        set => _session.CanCapture = value;
    }

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

    protected override void OnDispose()
    {
        _longPauseTimer.Dispose();
        CancelCapture();
    }

    protected override void OnKeyEvent(object? sender, KeyEventArgs args)
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
                args.Suppress = _session.HandleKey(WithCommitKey(args.Event));
            }
            _longPauseTimer.Change(_session.IsCapturing ? LongPause : Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            if (_session.IsCapturing) _ = RunInBackground("Following the caret", FollowCaretAsync);
        }
    }

    private async Task FollowCaretAsync()
    {
        if (await ReadSettledFieldAsync() is not { } field) return;
        lock (_gate) _session.UpdateCaret(field);
    }

    protected override void OnFocusChanged(object? sender, FocusedField? field)
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
    private async Task ReplaceQueryAsync(FocusedField captureField, string glyph, string typedRun)
    {
        // A browser can show the last letters of the query a moment after the commit key arrives.
        // Measured from a read that is still catching up, the run would be too short and the edit
        // would be refused, so the read waits briefly for the whole run. A run that never appears
        // in full lost a key or was changed by the host, and then the measured run is the right one.
        var field = Focus.Refresh() ?? captureField;
        for (int poll = 0; poll < RunPublishPolls && !EmojiQueryRun.ShowsTypedRun(field, typedRun); poll++)
        {
            await Task.Delay(RunPublishPollMs).ConfigureAwait(false);
            field = Focus.Refresh() ?? field;
        }
        int deleteCount = EmojiQueryRun.DeleteCount(field, typedRun.Length);
        await ReplaceTypedAsync(field, deleteCount, glyph, "Emoji").ConfigureAwait(false);
    }

    private const int RunPublishPolls = 8;
    private const int RunPublishPollMs = 20;

    /// <summary>Marshals the session's panel calls onto the popup's UI thread.</summary>
    private sealed class DispatchedPanel(EmojiPopupWindow popup, Dispatcher dispatcher) : IEmojiPickerPanel
    {
        public void Show(string query, IReadOnlyList<EmojiMatch> matches, int selectedIndex, ScreenRect caretRect) =>
            dispatcher.BeginInvoke(() => popup.ShowMatches(query, matches, selectedIndex, caretRect));

        public void SetSelectedIndex(int index) => dispatcher.BeginInvoke(() => popup.SetSelectedIndex(index));

        public void Hide() => dispatcher.BeginInvoke(() => popup.Hide());
    }
}
