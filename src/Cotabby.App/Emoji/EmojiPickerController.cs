using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Cotabby.Core.Emoji;
using Cotabby.Core.Focus;
using Cotabby.Core.Input;
using Cotabby.Core.Insertion;
using Microsoft.Extensions.Logging;

namespace Cotabby.App.Emoji;

/// <summary>
/// Wires keyboard events into the emoji trigger state machine and drives the
/// popup window + final insertion. Mirrors the macOS port's
/// <c>EmojiPickerController</c>: observes the global keyboard stream
/// alongside the suggestion coordinator, owns no focus/UIA work of its own,
/// only consumes events when a trigger is active.
/// </summary>
/// <remarks>
/// Two notable Windows constraints vs the macOS port:
/// <list type="bullet">
///   <item>We can't intercept the trailing <c>:</c> the way macOS does — by
///   the time the hook callback sees it, the host already received it. So on
///   commit we backspace over <c>":query:"</c> and paste the emoji; the user
///   sees their typed colons disappear and the emoji appear in their place.</item>
///   <item>We never suppress events from the hook for emoji — Tab/Enter still
///   reach the host as usual. The picker is purely additive: when active and
///   visible, the *next* commit-eligible key replaces the typed
///   <c>:query</c> with the chosen glyph.</item>
/// </list>
/// </remarks>
public sealed class EmojiPickerController : IDisposable
{
    private readonly IKeyboardHook _hook;
    private readonly IFocusTracker _focus;
    private readonly ITextInserter _inserter;
    private readonly EmojiPopupWindow _popup;
    private readonly Dispatcher _dispatcher;
    private readonly ILogger<EmojiPickerController> _logger;

    private readonly EmojiTriggerStateMachine _state = new();
    private List<EmojiEntry> _matches = new();
    private int _selected;
    private FocusedField? _triggerField;

    public bool Enabled { get; set; } = true;

    public EmojiPickerController(
        IKeyboardHook hook,
        IFocusTracker focus,
        ITextInserter inserter,
        EmojiPopupWindow popup,
        Dispatcher dispatcher,
        ILogger<EmojiPickerController> logger)
    {
        _hook = hook;
        _focus = focus;
        _inserter = inserter;
        _popup = popup;
        _dispatcher = dispatcher;
        _logger = logger;
        _hook.KeyEvent += OnKeyEvent;
    }

    public void Dispose()
    {
        _hook.KeyEvent -= OnKeyEvent;
        _dispatcher.BeginInvoke(() => _popup.Hide());
    }

    private void OnKeyEvent(object? sender, KeyEventArgs args)
    {
        if (!Enabled) return;

        // We need to know what the trigger anchor field is so we can backspace
        // the typed colons + query on commit. Refresh focus lazily — only when
        // we're about to enter an active state.
        if (!_state.IsActive && args.Event.Kind == KeyKind.Character && args.Event.Character == ':')
        {
            _triggerField = _focus.Refresh();
            if (_triggerField is null || _triggerField.IsSecure)
            {
                return; // Don't start in fields where we can't insert anyway.
            }
        }

        var outcome = _state.Apply(args.Event);
        switch (outcome)
        {
            case EmojiTriggerStateMachine.Outcome.QueryChanged:
                RefreshPopup();
                return;
            case EmojiTriggerStateMachine.Outcome.Commit:
                if (_matches.Count > 0)
                {
                    args.Suppress = true;
                    var entry = _matches[_selected];
                    _ = CommitAsync(entry);
                }
                ClearPopup();
                return;
            case EmojiTriggerStateMachine.Outcome.Cancel:
                ClearPopup();
                return;
            case EmojiTriggerStateMachine.Outcome.None:
            default:
                return;
        }
    }

    private void RefreshPopup()
    {
        _matches = EmojiMatcher.Search(_state.Query, max: 6);
        _selected = 0;
        // Re-resolve the anchor each tick — the caret moves as the user types.
        var field = _focus.Refresh() ?? _triggerField;
        var anchor = field?.CaretRect ?? new ScreenRect(100, 100, 1, 20);
        _dispatcher.BeginInvoke(() => _popup.Show(_matches, _state.Query, anchor, _selected));
    }

    private void ClearPopup()
    {
        _matches = new List<EmojiEntry>();
        _selected = 0;
        _triggerField = null;
        _dispatcher.BeginInvoke(() => _popup.Hide());
    }

    private async Task CommitAsync(EmojiEntry entry)
    {
        try
        {
            var field = _focus.Refresh() ?? _triggerField;
            if (field is null) return;

            // The user typed ":query:" — eat those characters with backspaces
            // before inserting the glyph. Length of ":query:" = query + 2.
            // We do not have a "delete N chars" primitive in ITextInserter,
            // so issue the backspace via the same SendInput channel as text.
            // The inserter's clipboard-paste path always wraps in BlockInput,
            // making the {backspace*N}{paste} sequence atomic from the host's
            // perspective.
            int eat = _state.QueryAtCommitLength;
            await EatCharsAsync(eat, CancellationToken.None);
            await _inserter.InsertAsync(field, entry.Glyph, CancellationToken.None);

            _logger.LogInformation(
                "Emoji committed: query=\":{Query}:\" → {Glyph} (eaten chars={Eat})",
                entry.Name, entry.Glyph, eat);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Emoji commit failed.");
        }
    }

    private static async Task EatCharsAsync(int count, CancellationToken ct)
    {
        if (count <= 0) return;
        await Task.Run(() =>
        {
            for (int i = 0; i < count; i++)
            {
                if (ct.IsCancellationRequested) return;
                SendBackspace();
            }
        }, ct);
        // Give the host a few ms to publish the backspaces back into its model
        // before we paste — without this, the paste lands BEFORE the deletes
        // and we end up with both ":query:" and the emoji visible.
        await Task.Delay(40, ct);
    }

    private static void SendBackspace()
    {
        // VK_BACK is 0x08.
        INPUT[] inputs = new INPUT[2];
        inputs[0].type = 1; // INPUT_KEYBOARD
        inputs[0].u.ki.wVk = 0x08;
        inputs[0].u.ki.dwFlags = 0;
        inputs[1].type = 1;
        inputs[1].u.ki.wVk = 0x08;
        inputs[1].u.ki.dwFlags = 2; // KEYEVENTF_KEYUP
        _ = SendInput((uint)inputs.Length, inputs, System.Runtime.InteropServices.Marshal.SizeOf<INPUT>());
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct INPUT { public uint type; public INPUTUNION u; }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit)]
    private struct INPUTUNION
    {
        [System.Runtime.InteropServices.FieldOffset(0)] public KEYBDINPUT ki;
        [System.Runtime.InteropServices.FieldOffset(0)] public MOUSEINPUT mi;
        [System.Runtime.InteropServices.FieldOffset(0)] public HARDWAREINPUT hi;
    }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo;
    }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct HARDWAREINPUT { public uint uMsg; public ushort wParamL, wParamH; }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
}
