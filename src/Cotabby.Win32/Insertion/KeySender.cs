using System.Runtime.InteropServices;
using Cotabby.Core.Focus;
using Cotabby.Core.Insertion;
using static Cotabby.Win32.Interop.NativeMethods;

namespace Cotabby.Win32.Insertion;

/// <summary>Synthesizes virtual key presses into the focused application.</summary>
public static class KeySender
{
    /// <summary>Presses Backspace <paramref name="count"/> times as one uninterruptible SendInput batch.</summary>
    public static void SendBackspaces(int count)
    {
        if (count <= 0) return;

        var inputs = new INPUT[count * 2];
        for (int i = 0; i < inputs.Length; i++)
        {
            inputs[i] = new INPUT
            {
                type = INPUT_KEYBOARD,
                U = new INPUTUNION
                {
                    ki = new KEYBDINPUT { wVk = VK_BACK, dwFlags = i % 2 == 0 ? 0 : KEYEVENTF_KEYUP },
                },
            };
        }
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    /// <summary>
    /// Deletes <paramref name="deleteCount"/> characters before the caret and types
    /// <paramref name="text"/> in their place. The pause lets the host apply the deletes first.
    /// </summary>
    public static async Task<bool> ReplaceBeforeCaretAsync(
        ITextInserter inserter, FocusedField field, int deleteCount, string text)
    {
        SendBackspaces(deleteCount);
        await Task.Delay(DeleteSettleMs).ConfigureAwait(false);
        return await inserter.InsertAsync(field, text, CancellationToken.None).ConfigureAwait(false);
    }

    private const int DeleteSettleMs = 40;
}
