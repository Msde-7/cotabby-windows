using System.Runtime.InteropServices;
using Cotabby.Core.Focus;
using Cotabby.Core.Insertion;
using Cotabby.Core.Support;
using Cotabby.Win32.Clipboard;
using Microsoft.Extensions.Logging;
using static Cotabby.Win32.Interop.NativeMethods;
using WpfClipboard = System.Windows.Clipboard;

namespace Cotabby.Win32.Insertion;

/// <summary>
/// <see cref="ITextInserter"/> that types short single-line text as Unicode keystrokes and pastes
/// everything else through the clipboard.
/// </summary>
/// <remarks>
/// Long keystroke bursts drop characters in many hosts. Notepad accepts only about the first ten
/// of forty before its input queue overflows. A paste goes through the host's own paste handler,
/// which inserts any length at once.
/// <para>
/// The paste saves the clipboard text, writes the suggestion, sends Ctrl+V and restores the saved
/// text. <c>BlockInput</c> brackets it so physical keystrokes cannot interleave. Clipboard
/// content that is not text, such as an image or files, is lost.
/// </para>
/// </remarks>
public sealed class SendInputTextInserter : ITextInserter
{
    private readonly ILogger<SendInputTextInserter> _logger;
    private readonly ClipboardTextReader? _clipboardContext;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <param name="clipboardContext">Told about every paste, so it does not take one for a copy by the user.</param>
    public SendInputTextInserter(ILogger<SendInputTextInserter> logger, ClipboardTextReader? clipboardContext = null)
    {
        _logger = logger;
        _clipboardContext = clipboardContext;
    }

    /// <summary>
    /// Text at or below this many UTF-16 units is typed in one SendInput batch and never touches
    /// the clipboard. Word accepts are almost always under it. One call with the whole array
    /// cannot be interleaved with other input.
    /// </summary>
    private const int MaxSendInputChars = 24;

    /// <summary>Lets the host apply the deletes before the replacement text arrives.</summary>
    private const int DeleteSettleMs = 40;

    public async Task<bool> ReplaceAsync(FocusedField target, int deleteCount, string text, CancellationToken ct)
    {
        KeySender.SendBackspaces(deleteCount);
        await Task.Delay(DeleteSettleMs, ct).ConfigureAwait(false);
        return await InsertAsync(target, text, ct).ConfigureAwait(false);
    }

    public async Task<bool> InsertAsync(FocusedField target, string text, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(text)) return true;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ct.ThrowIfCancellationRequested();

            if (text.Length <= MaxSendInputChars && !text.Contains('\n'))
            {
                _logger.LogInformation(
                    "Inserter (unicode) {Len} chars: \"{Text}\" target.proc={Proc}",
                    text.Length, LogText.Escape(text), target.ProcessName);
                SendUnicodeString(text);
                return true;
            }

            _logger.LogInformation(
                "Inserter (clipboard paste) received {Len} chars: \"{Text}\" hex={Hex} target.pid={Pid} target.proc={Proc}",
                text.Length, LogText.Escape(text), LogText.Hex(text), target.ProcessId, target.ProcessName);

            // Keeps physical keystrokes from interleaving with the Ctrl+V.
            bool blocked = BlockInput(true);
            string? savedClipboard = null;
            try
            {
                // OLE clipboard calls need an STA thread.
                savedClipboard = await OnStaThreadAsync(TryReadClipboardText).ConfigureAwait(false);

                _clipboardContext?.BeginOwnWrite();
                bool wrote = await OnStaThreadAsync(() => TryWriteClipboardText(text)).ConfigureAwait(false);
                if (!wrote)
                {
                    _logger.LogWarning("Clipboard write failed; cannot paste.");
                    return false;
                }

                // Lets the host see the clipboard update before the paste.
                await Task.Delay(15, ct).ConfigureAwait(false);

                SendCtrlV();

                // The host must consume the paste before the clipboard is restored. 80 ms covers
                // Notepad and Electron.
                await Task.Delay(80, ct).ConfigureAwait(false);
            }
            finally
            {
                // Restored even when the paste failed, so the suggestion never stays on the
                // user's clipboard.
                bool restored = false;
                if (savedClipboard is not null)
                {
                    try { restored = await OnStaThreadAsync(() => TryWriteClipboardText(savedClipboard)).ConfigureAwait(false); }
                    catch (Exception ex) { _logger.LogWarning(ex, "Clipboard restore failed."); }
                }
                _clipboardContext?.EndOwnWrite(restored);
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
    /// Runs <paramref name="func"/> on a fresh STA thread. The caller may be a thread-pool
    /// thread, which is not STA, and the work is a couple of cheap OLE calls.
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
            return WpfClipboard.ContainsText() ? WpfClipboard.GetText() : null;
        }
        catch (Exception) { return null; }
    }

    private static bool TryWriteClipboardText(string text)
    {
        try
        {
            // With copy set the clipboard owns its own copy of the string, which survives this
            // process exiting.
            WpfClipboard.SetDataObject(text, copy: true);
            return true;
        }
        catch (Exception) { return false; }
    }

    private const ushort VK_CONTROL = 0x11;
    private const ushort VK_V = 0x56;

    /// <summary>
    /// Sends Ctrl+V as virtual keys. KEYEVENTF_UNICODE does not apply because Ctrl+V is a
    /// shortcut and not a character.
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

    /// <summary>
    /// Types <paramref name="text"/> as one SendInput batch of KEYEVENTF_UNICODE pairs. UTF-16
    /// units pass through unchanged, so surrogate pairs arrive intact.
    /// </summary>
    private static void SendUnicodeString(string text)
    {
        var inputs = new INPUT[text.Length * 2];
        for (int i = 0; i < text.Length; i++)
        {
            inputs[i * 2] = MakeUnicodeInput(text[i], keyUp: false);
            inputs[i * 2 + 1] = MakeUnicodeInput(text[i], keyUp: true);
        }
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    private static INPUT MakeUnicodeInput(char c, bool keyUp) => new()
    {
        type = INPUT_KEYBOARD,
        U = new INPUTUNION
        {
            ki = new KEYBDINPUT
            {
                wVk = 0,
                wScan = c,
                dwFlags = KEYEVENTF_UNICODE | (keyUp ? KEYEVENTF_KEYUP : 0),
                time = 0,
                dwExtraInfo = IntPtr.Zero,
            },
        },
    };

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
}
