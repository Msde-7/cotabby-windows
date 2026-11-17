using System.Runtime.InteropServices;
using static Cotabby.Win32.Interop.NativeMethods;

namespace Cotabby.Win32.Insertion;

/// <summary>
/// Sends <see cref="SyntheticKeys"/> batches to the focused application. One SendInput call with
/// the whole batch cannot be interleaved with other input.
/// </summary>
/// <remarks>
/// Every event carries <see cref="CotabbyInputMarker"/>, which is how the keyboard hook tells
/// Cotabby's own keys from injected input it should handle. Each method returns false when
/// Windows did not take the whole batch.
/// </remarks>
public static class KeySender
{
    public static bool SendBackspaces(int count) =>
        count <= 0 || Send(SyntheticKeys.Backspaces(HeldModifiers(), count));

    public static bool SendText(string text) => Send(SyntheticKeys.Text(HeldModifiers(), text));

    public static bool SendPaste() => Send(SyntheticKeys.Paste(HeldModifiers()));

    /// <inheritdoc cref="SyntheticKeys.MenuMask"/>
    public static bool SendMenuMask() => Send(SyntheticKeys.MenuMask());

    private static IEnumerable<ushort> HeldModifiers() =>
        SyntheticKeys.Modifiers.Where(vk => (GetAsyncKeyState(vk) & 0x8000) != 0);

    private static bool Send(List<SyntheticKey> keys)
    {
        if (keys.Count == 0) return true;
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
                    ki = new KEYBDINPUT
                    {
                        wVk = key.VirtualKey,
                        wScan = key.Character,
                        dwFlags = flags,
                        dwExtraInfo = CotabbyInputMarker,
                    },
                },
            };
        }
        return SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>()) == inputs.Length;
    }
}
