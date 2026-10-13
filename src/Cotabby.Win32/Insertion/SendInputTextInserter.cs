using Cotabby.Core.Focus;
using Cotabby.Core.Insertion;
using Cotabby.Core.Support;
using Cotabby.Win32.Clipboard;
using Microsoft.Extensions.Logging;
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
/// text. Clipboard content that is not text, such as an image or files, is lost and the
/// clipboard is left empty.
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

            // The text itself is logged at Debug only, so the default log never holds it.
            bool typed = SyntheticKeys.CanType(text);
            _logger.LogInformation(
                "Inserter ({Path}) {Len} chars target.pid={Pid} target.proc={Proc}",
                typed ? "unicode" : "clipboard paste", text.Length, target.ProcessId, target.ProcessName);
            _logger.LogDebug("Inserter text \"{Text}\" hex={Hex}", LogText.Escape(text), LogText.Hex(text));

            if (typed)
            {
                KeySender.SendText(text);
                return true;
            }

            return await PasteAsync(text, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<bool> PasteAsync(string text, CancellationToken ct)
    {
        // OLE clipboard calls need an STA thread.
        string? savedClipboard = await OnStaThreadAsync(TryReadClipboardText).ConfigureAwait(false);

        _clipboardContext?.BeginOwnWrite();
        bool wrote = false;
        try
        {
            wrote = await OnStaThreadAsync(() => TryWriteClipboardText(text)).ConfigureAwait(false);
            if (!wrote)
            {
                _logger.LogWarning("Clipboard write failed; cannot paste.");
                return false;
            }

            // Lets the host see the clipboard update before the paste.
            await Task.Delay(15, ct).ConfigureAwait(false);

            KeySender.SendPaste();

            // The host must consume the paste before the clipboard is restored. 80 ms covers
            // Notepad and Electron.
            await Task.Delay(80, ct).ConfigureAwait(false);
            return true;
        }
        finally
        {
            // Runs even when the paste failed, so the suggestion never stays on the user's
            // clipboard. A clipboard that held no text is emptied.
            bool restored = true;
            if (savedClipboard is not null)
            {
                restored = await OnStaThreadAsync(() => TryWriteClipboardText(savedClipboard)).ConfigureAwait(false);
            }
            else if (wrote)
            {
                restored = await OnStaThreadAsync(TryClearClipboard).ConfigureAwait(false);
            }
            if (!restored) _logger.LogWarning("Clipboard restore failed.");
            _clipboardContext?.EndOwnWrite(restored);
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

    private static bool TryClearClipboard()
    {
        try
        {
            WpfClipboard.Clear();
            return true;
        }
        catch (Exception) { return false; }
    }
}
