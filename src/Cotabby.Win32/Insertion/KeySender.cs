using System.Runtime.InteropServices;
using Cotabby.Core.Input;
using static Cotabby.Win32.Interop.NativeMethods;

namespace Cotabby.Win32.Insertion;

/// <summary>
/// Sends <see cref="SyntheticKeys"/> batches to the focused application. One SendInput call with
/// the whole batch cannot be interleaved with other input.
/// </summary>
/// <remarks>
/// Every event carries <see cref="CotabbyInputMarker"/>, or <see cref="CotabbyRestoreMarker"/> on a
/// modifier pressed again, which is how the keyboard hook tells Cotabby's own keys from injected
/// input it should handle. Each method returns false when
/// Windows did not take the whole batch. Keys the hook held back are sent again with
/// <see cref="CotabbyReplayMarker"/> instead.
/// </remarks>
public static class KeySender
{
    public static bool SendBackspaces(int count) =>
        count <= 0 || Send(SyntheticKeys.Backspaces(HeldModifiers(), count));

    public static bool SendText(string text) => Send(SyntheticKeys.Text(HeldModifiers(), text));

    public static bool SendPaste() => Send(SyntheticKeys.Paste(HeldModifiers()));

    /// <inheritdoc cref="SyntheticKeys.MenuMask"/>
    public static bool SendMenuMask() => Send(SyntheticKeys.MenuMask());

    /// <summary>
    /// Sends keys the hook held back, as they were typed, marked so the hook hands them to its
    /// handlers. Returns how many Windows took.
    /// </summary>
    public static int Replay(ReadOnlySpan<RawKey> keys) => Send(keys, CotabbyReplayMarker);

    /// <summary>Sends keys straight to the focused application. The hook lets them pass untouched.</summary>
    public static int PassThrough(ReadOnlySpan<RawKey> keys) => Send(keys, CotabbyInputMarker);

    /// <summary>The KEYBDINPUT fields that reproduce a key event the hook received.</summary>
    public static (ushort VirtualKey, ushort ScanCode, uint Flags) InputFor(RawKey key)
    {
        uint flags = key.IsKeyUp ? KEYEVENTF_KEYUP : 0;
        // Windows reports a Unicode keystroke as VK_PACKET with the UTF-16 unit in the scan code.
        if (key.IsPacket) return (0, (ushort)key.ScanCode, flags | KEYEVENTF_UNICODE);
        if (key.IsExtended) flags |= KEYEVENTF_EXTENDEDKEY;
        return ((ushort)key.VirtualKey, (ushort)key.ScanCode, flags);
    }

    private static int Send(ReadOnlySpan<RawKey> keys, IntPtr marker)
    {
        if (keys.IsEmpty) return 0;
        var inputs = new INPUT[keys.Length];
        for (int i = 0; i < inputs.Length; i++)
        {
            var (vk, scan, flags) = InputFor(keys[i]);
            inputs[i] = new INPUT
            {
                type = INPUT_KEYBOARD,
                U = new INPUTUNION
                {
                    ki = new KEYBDINPUT { wVk = vk, wScan = scan, dwFlags = flags, dwExtraInfo = marker },
                },
            };
        }
        return (int)SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

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
                        dwExtraInfo = key.Restores ? CotabbyRestoreMarker : CotabbyInputMarker,
                    },
                },
            };
        }
        return SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>()) == inputs.Length;
    }
}
