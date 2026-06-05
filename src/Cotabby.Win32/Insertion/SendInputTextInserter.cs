using System.Runtime.InteropServices;
using System.Windows;
using Cotabby.Core.Focus;
using Cotabby.Core.Insertion;
using Microsoft.Extensions.Logging;
using static Cotabby.Win32.Interop.NativeMethods;

namespace Cotabby.Win32.Insertion;

/// <summary>
/// <see cref="ITextInserter"/> that pastes via the clipboard: save the
/// current clipboard, write the suggestion, synthesize Ctrl+V to make the
/// host's standard paste handler insert it atomically, then restore the
/// original clipboard.
/// </summary>
/// <remarks>
/// Why clipboard paste instead of per-char SendInput Unicode:
/// the obvious SendInput KEYEVENTF_UNICODE approach drops events in many
/// hosts — Notepad in particular accepts only the first ~10 of a 40-char
/// burst before its input queue overflows and silently discards the rest
/// (verified via post-insertion UIA reads). Electron-based editors fare a
/// little better but still drop characters under load. A single Ctrl+V
/// keystroke routes through the host's paste pipeline which is built for
/// atomic multi-character insertion of arbitrary length.
///
/// Why bracket with <c>BlockInput</c>:
/// physical keystrokes arriving between our Ctrl+V keydown and Ctrl+V
/// keyup, or between paste and clipboard restore, would otherwise be
/// processed as live input and interleave with the paste.
///
/// Trade-off: clipboard contents flicker for ~50ms. We save and restore
/// the previous text contents; anything richer (images, files) is lost
/// across the round-trip — an acceptable cost for reliable insertion.
/// </remarks>
public sealed class SendInputTextInserter : ITextInserter
{
    private readonly ILogger<SendInputTextInserter> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SendInputTextInserter(ILogger<SendInputTextInserter> logger) => _logger = logger;

    public async Task<bool> InsertAsync(FocusedField target, string text, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(text)) return true;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ct.ThrowIfCancellationRequested();

            _logger.LogInformation(
                "Inserter (clipboard paste) received {Len} chars: \"{Text}\" hex={Hex} target.pid={Pid} target.proc={Proc}",
                text.Length, EscapeFull(text), HexDump(text), target.ProcessId, target.ProcessName);

            // BlockInput keeps physical keystrokes from interleaving with the
            // synthetic Ctrl+V we're about to send. ~50ms blocked total.
            bool blocked = BlockInput(true);
            string? savedClipboard = null;
            try
            {
                // Snapshot existing clipboard so the round-trip is invisible.
                // STA is required for OLE clipboard ops — InsertAsync is called
                // from the UI thread in normal operation, but we go through the
                // dispatcher explicitly to be safe.
                savedClipboard = await OnStaThreadAsync(TryReadClipboardText).ConfigureAwait(false);

                bool wrote = await OnStaThreadAsync(() => TryWriteClipboardText(text)).ConfigureAwait(false);
                if (!wrote)
                {
                    _logger.LogWarning("Clipboard write failed; cannot paste.");
                    return false;
                }

                // Give the host a beat to see the clipboard update before paste.
                await Task.Delay(15, ct).ConfigureAwait(false);

                SendCtrlV();

                // Wait long enough for the host to consume the paste before we
                // restore the clipboard. 80ms covers Notepad and Electron paste
                // pipelines comfortably.
                await Task.Delay(80, ct).ConfigureAwait(false);
            }
            finally
            {
                // Restore prior clipboard contents (or clear if nothing was
                // there originally). Always do this even if paste failed so we
                // don't leave the user's clipboard pointing at our suggestion.
                if (savedClipboard is not null)
                {
                    try { await OnStaThreadAsync(() => TryWriteClipboardText(savedClipboard)).ConfigureAwait(false); }
                    catch (Exception ex) { _logger.LogWarning(ex, "Clipboard restore failed."); }
                }
                if (blocked) BlockInput(false);
            }

            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Run <paramref name="func"/> on a fresh STA thread. WPF's UI thread is
    /// STA but calling InsertAsync from there blocks on the SemaphoreSlim
    /// above — so spin up a one-shot STA helper. The work itself is cheap
    /// (couple of OLE calls), so the thread creation cost is fine.
    /// </summary>
    private static Task<T> OnStaThreadAsync<T>(Func<T> func)
    {
        var tcs = new TaskCompletionSource<T>();
        var t = new Thread(() =>
        {
            try { tcs.SetResult(func()); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.IsBackground = true;
        t.Start();
        return tcs.Task;
    }

    private static string? TryReadClipboardText()
    {
        try
        {
            return Clipboard.ContainsText() ? Clipboard.GetText() : null;
        }
        catch (Exception) { return null; }
    }

    private static bool TryWriteClipboardText(string text)
    {
        try
        {
            // SetDataObject(copy: true) leaves the clipboard owning a separate
            // copy of the string so our process can exit without invalidating
            // the host's just-read paste.
            Clipboard.SetDataObject(text, copy: true);
            return true;
        }
        catch (Exception) { return false; }
    }

    private const ushort VK_CONTROL = 0x11;
    private const ushort VK_V = 0x56;

    /// <summary>
    /// Send Ctrl-down, V-down, V-up, Ctrl-up via SendInput. We use regular
    /// virtual-key SendInput rather than KEYEVENTF_UNICODE because Ctrl+V is
    /// a virtual-key shortcut, not a character.
    /// </summary>
    private static void SendCtrlV()
    {
        var inputs = new INPUT[]
        {
            MakeVkInput(VK_CONTROL, keyUp: false),
            MakeVkInput(VK_V,       keyUp: false),
            MakeVkInput(VK_V,       keyUp: true),
            MakeVkInput(VK_CONTROL, keyUp: true),
        };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    private static INPUT MakeVkInput(ushort vk, bool keyUp) => new()
    {
        type = INPUT_KEYBOARD,
        U = new INPUTUNION
        {
            ki = new KEYBDINPUT
            {
                wVk = vk,
                wScan = 0,
                dwFlags = keyUp ? KEYEVENTF_KEYUP : 0,
                time = 0,
                dwExtraInfo = IntPtr.Zero,
            },
        },
    };

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
}
