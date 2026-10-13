using System.Runtime.InteropServices;
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
}
