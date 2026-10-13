using System.Runtime.InteropServices;
using static Cotabby.Win32.Interop.NativeMethods;

namespace Cotabby.Win32.Insertion;

/// <summary>
/// Sends <see cref="SyntheticKeys"/> batches to the focused application. One SendInput call with
/// the whole batch cannot be interleaved with other input.
/// </summary>
internal static class KeySender
{
    public static void SendBackspaces(int count)
    {
        if (count > 0) Send(SyntheticKeys.Backspaces(HeldModifiers(), count));
    }

    public static void SendText(string text) => Send(SyntheticKeys.Text(HeldModifiers(), text));

    public static void SendPaste() => Send(SyntheticKeys.Paste(HeldModifiers()));

    private static IEnumerable<ushort> HeldModifiers() =>
        SyntheticKeys.Modifiers.Where(vk => (GetAsyncKeyState(vk) & 0x8000) != 0);

    private static void Send(List<SyntheticKey> keys)
    {
        var inputs = new INPUT[keys.Count];
        for (int i = 0; i < inputs.Length; i++)
        {
            var key = keys[i];
            uint flags = key.KeyUp ? KEYEVENTF_KEYUP : 0;
            if (key.VirtualKey == 0) flags |= KEYEVENTF_UNICODE;
            else if (key.VirtualKey is VK_RCONTROL or VK_RMENU or VK_LWIN or VK_RWIN) flags |= KEYEVENTF_EXTENDEDKEY;

            inputs[i] = new INPUT
            {
                type = INPUT_KEYBOARD,
                U = new INPUTUNION
                {
                    ki = new KEYBDINPUT { wVk = key.VirtualKey, wScan = key.Character, dwFlags = flags },
                },
            };
        }
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }
}
