using Cotabby.Core.Focus;
using Cotabby.Core.Insertion;
using Cotabby.Core.Support;
using Cotabby.Win32.Clipboard;
using Microsoft.Extensions.Logging;

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
/// The paste saves the clipboard's text, rich text, HTML and copied files, writes the
/// suggestion, sends Ctrl+V and later puts the saved content back. The restore waits long enough
/// for a slow host to read the clipboard, and it is skipped when anything else wrote to the
/// clipboard meanwhile, so a copy the user made in that time is kept. When the clipboard cannot
/// be read, single-line text is typed in short bursts instead and multi-line text is not
/// inserted, since typing a line break as Enter could send a chat message.
/// </para>
/// <para>
/// Before every batch of keys the foreground window is checked against the target field, so
/// keys never land in a window the user switched to.
/// </para>
/// </remarks>
public sealed class SendInputTextInserter : ITextInserter
{
    private readonly ILogger<SendInputTextInserter> _logger;
    private readonly ClipboardTextReader? _clipboardContext;

    // Held from the first key of an insertion until its clipboard restore, so the next paste
    // never saves the suggestion as the user's clipboard.
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <param name="clipboardContext">Told about every paste, so it does not take one for a copy by the user.</param>
    public SendInputTextInserter(ILogger<SendInputTextInserter> logger, ClipboardTextReader? clipboardContext = null)
    {
        _logger = logger;
        _clipboardContext = clipboardContext;
    }

    /// <summary>Lets the host apply the deletes before the replacement text arrives.</summary>
    private const int DeleteSettleMs = 40;

    /// <summary>Lets the host see the clipboard update before the paste.</summary>
    private const int ClipboardSettleMs = 15;

    /// <summary>
    /// How long the host gets to read the clipboard after Ctrl+V before the user's content goes
    /// back. Notepad and Electron need under 100 ms, but a busy or remote host can take far longer.
    /// </summary>
    private const int RestoreSettleMs = 500;

    private const int RestoreAttempts = 3;
    private const int RestoreRetryMs = 150;

    /// <summary>Pause between typed bursts, so the host's input queue drains.</summary>
    private const int BurstGapMs = 20;

    public async Task<bool> ReplaceAsync(FocusedField target, int deleteCount, string text, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        Task? restore = null;
        try
        {
            if (deleteCount > 0)
            {
                if (!Send(target, () => KeySender.SendBackspaces(deleteCount), "backspaces")) return false;
                await Task.Delay(DeleteSettleMs, ct).ConfigureAwait(false);
            }
            (bool inserted, restore) = await InsertLockedAsync(target, text, ct).ConfigureAwait(false);
            return inserted;
        }
        finally
        {
            ReleaseAfter(restore);
        }
    }

    public async Task<bool> InsertAsync(FocusedField target, string text, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(text)) return true;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        Task? restore = null;
        try
        {
            (bool inserted, restore) = await InsertLockedAsync(target, text, ct).ConfigureAwait(false);
            return inserted;
        }
        finally
        {
            ReleaseAfter(restore);
        }
    }

    /// <summary>Releases the gate now, or once a pending clipboard restore has finished.</summary>
    private void ReleaseAfter(Task? restore)
    {
        if (restore is null)
        {
            _gate.Release();
            return;
        }
        _ = restore.ContinueWith(_ => _gate.Release(), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>Inserts with the gate held. A paste also returns its pending clipboard restore.</summary>
    private async Task<(bool Inserted, Task? Restore)> InsertLockedAsync(
        FocusedField target, string text, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(text)) return (true, null);
        ct.ThrowIfCancellationRequested();

        // The text itself is logged at Debug only, so the default log never holds it.
        bool typed = SyntheticKeys.CanType(text);
        _logger.LogInformation(
            "Inserter ({Path}) {Len} chars target.pid={Pid} target.proc={Proc}",
            typed ? "unicode" : "clipboard paste", text.Length, target.ProcessId, target.ProcessName);
        _logger.LogDebug("Inserter text \"{Text}\" hex={Hex}", LogText.Escape(text), LogText.Hex(text));

        if (typed) return (Send(target, () => KeySender.SendText(text), "text"), null);
        return await PasteAsync(target, text, ct).ConfigureAwait(false);
    }

    private async Task<(bool Inserted, Task? Restore)> PasteAsync(FocusedField target, string text, CancellationToken ct)
    {
        if (!ForegroundTarget.Holds(target))
        {
            _logger.LogInformation("Paste skipped: the target field no longer has the keyboard.");
            return (false, null);
        }

        _clipboardContext?.BeginOwnWrite();
        ClipboardSnapshot? saved = null;
        uint ownSequence = 0;
        bool wrote = await Task.Run(() => ClipboardSwap.TryReplaceWithText(text, out saved, out ownSequence))
            .ConfigureAwait(false);
        if (!wrote)
        {
            _clipboardContext?.EndOwnWrite(restored: true);
            _logger.LogWarning("Clipboard could not be read or written, so the suggestion is not pasted.");
            return (await TypeInBurstsAsync(target, text, ct).ConfigureAwait(false), null);
        }

        // From here the restore always runs, even when the paste is cancelled or fails, so the
        // suggestion never stays on the user's clipboard.
        bool pasted;
        try
        {
            await Task.Delay(ClipboardSettleMs, ct).ConfigureAwait(false);
            pasted = Send(target, KeySender.SendPaste, "paste");
        }
        catch
        {
            await RestoreClipboardAsync(saved!, ownSequence, settle: false).ConfigureAwait(false);
            throw;
        }
        // Not awaited: the caller goes on while the host reads the clipboard, and the gate stays
        // held until the restore is done. Nothing was pasted when the send failed, so the restore
        // then runs at once.
        return (pasted, RestoreClipboardAsync(saved!, ownSequence, settle: pasted));
    }

    private async Task RestoreClipboardAsync(ClipboardSnapshot saved, uint ownSequence, bool settle)
    {
        bool ownTextGone = false;
        try
        {
            if (settle) await Task.Delay(RestoreSettleMs).ConfigureAwait(false);
            for (int attempt = 1; attempt <= RestoreAttempts; attempt++)
            {
                var outcome = await Task.Run(() => ClipboardSwap.TryRestore(saved, ownSequence)).ConfigureAwait(false);
                if (outcome == ClipboardRestoreOutcome.ChangedSince)
                {
                    _logger.LogInformation("Clipboard changed after the paste, so it was left as it is.");
                    ownTextGone = true;
                    return;
                }
                if (outcome == ClipboardRestoreOutcome.Restored)
                {
                    ownTextGone = true;
                    return;
                }
                if (attempt < RestoreAttempts) await Task.Delay(RestoreRetryMs).ConfigureAwait(false);
            }
            _logger.LogWarning("Clipboard restore failed.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Clipboard restore failed.");
        }
        finally
        {
            _clipboardContext?.EndOwnWrite(ownTextGone);
        }
    }

    /// <summary>
    /// Types single-line text in short bursts when it cannot be pasted. Multi-line text is left
    /// out: a line break would have to be sent as Enter, which submits forms and sends messages.
    /// </summary>
    private async Task<bool> TypeInBurstsAsync(FocusedField target, string text, CancellationToken ct)
    {
        if (SyntheticKeys.HasLineBreak(text)) return false;
        bool first = true;
        foreach (var burst in SyntheticKeys.Bursts(text))
        {
            if (!first) await Task.Delay(BurstGapMs, ct).ConfigureAwait(false);
            first = false;
            if (!Send(target, () => KeySender.SendText(burst), "text")) return false;
        }
        return true;
    }

    /// <summary>Sends one batch if the target still has the keyboard. False when it did not go out whole.</summary>
    private bool Send(FocusedField target, Func<bool> send, string what)
    {
        if (!ForegroundTarget.Holds(target))
        {
            _logger.LogInformation("Inserter stopped before {What}: the target field no longer has the keyboard.", what);
            return false;
        }
        if (send()) return true;
        _logger.LogWarning("SendInput did not take all {What} keys.", what);
        return false;
    }
}
