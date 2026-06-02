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

            var inputs = BuildInputs(text);
            uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
            if (sent != inputs.Length)
            {
                int err = Marshal.GetLastWin32Error();
                _logger.LogWarning(
                    "SendInput delivered {Sent}/{Total} events (last error {Err}). The host app may be elevated (UIPI) or non-focusable.",
                    sent, inputs.Length, err);
                return false;
            }
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static INPUT[] BuildInputs(string text)
    {
        // Worst case: every char emits a down + up (2 events). Surrogate pairs add
        // another 2 events per high surrogate. We over-allocate by counting chars.
        var inputs = new List<INPUT>(text.Length * 2);
        foreach (char c in text)
        {
            inputs.Add(MakeUnicodeInput(c, keyUp: false));
            inputs.Add(MakeUnicodeInput(c, keyUp: true));
        }
        return inputs.ToArray();
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
