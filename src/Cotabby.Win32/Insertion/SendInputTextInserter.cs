using System.Runtime.InteropServices;
using Cotabby.Core.Focus;
using Cotabby.Core.Insertion;
using Microsoft.Extensions.Logging;
using static Cotabby.Win32.Interop.NativeMethods;

namespace Cotabby.Win32.Insertion;

/// <summary>
/// <see cref="ITextInserter"/> backed by <c>SendInput</c> with
/// <c>KEYEVENTF_UNICODE</c>. We avoid UIA <c>ValuePattern.SetValue</c> because
/// it replaces the entire field content, which is wrong for caret-position
/// insertions and breaks undo history in most editors. Synthesized keystrokes
/// route through the host app's normal input pipeline and respect IME state,
/// composition windows, and per-app undo.
/// </summary>
/// <remarks>
/// Caveats:
///
/// - <c>KEYEVENTF_UNICODE</c> bypasses the keyboard layout, so the host app's
///   <c>WM_KEYDOWN</c>/<c>WM_KEYUP</c> handlers may not see a virtual key —
///   most apps consume the resulting <c>WM_CHAR</c> messages anyway. A handful
///   of games and key-state-driven apps will not.
/// - Characters outside the BMP are emitted as a UTF-16 surrogate pair: two
///   keydown events followed by two keyup events.
/// - We mark events with <c>LLKHF_INJECTED</c> via Windows itself; our own
///   keyboard hook filters them out so the Tab acceptance loop doesn't recurse.
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

            // SendInput in a single big batch reliably loses events on some
            // Windows builds — particularly under input throttling and when
            // the target window's WndProc takes more than a few µs per
            // WM_CHAR. Symptom we saw: 49-character suggestion arrived at
            // the host as 'oooooo ddddd' — only a sparse handful of the
            // synthesized keystrokes survived. Send one character per
            // SendInput call with a 1ms yield between, and verify each call's
            // delivery count.
            int total = 0, failed = 0;
            foreach (char c in text)
            {
                ct.ThrowIfCancellationRequested();
                var pair = new[]
                {
                    MakeUnicodeInput(c, keyUp: false),
                    MakeUnicodeInput(c, keyUp: true),
                };
                uint sent = SendInput((uint)pair.Length, pair, Marshal.SizeOf<INPUT>());
                if (sent != pair.Length)
                {
                    failed++;
                    int err = Marshal.GetLastWin32Error();
                    _logger.LogWarning(
                        "SendInput dropped char '{Ch}' (sent {Sent}/{Total}, err {Err}).",
                        c, sent, pair.Length, err);
                }
                total++;
                // Tiny yield. ~1ms between chars gives the target's message
                // pump time to drain WM_CHAR before the next pair arrives.
                if ((total & 7) == 0)
                {
                    await Task.Delay(1, ct).ConfigureAwait(false);
                }
            }
            if (failed > 0)
            {
                _logger.LogWarning(
                    "SendInput insertion dropped {Failed}/{Total} chars for target host pid={Pid}.",
                    failed, total, target.ProcessId);
                return false;
            }
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static INPUT MakeUnicodeInput(char unit, bool keyUp) => new()
    {
        type = INPUT_KEYBOARD,
        U = new INPUTUNION
        {
            ki = new KEYBDINPUT
            {
                wVk = 0,
                wScan = unit,
                dwFlags = KEYEVENTF_UNICODE | (keyUp ? KEYEVENTF_KEYUP : 0),
                time = 0,
                dwExtraInfo = IntPtr.Zero,
            },
        },
    };
}
