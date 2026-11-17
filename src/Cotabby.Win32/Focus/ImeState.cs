using System.Runtime.InteropServices;
using Cotabby.Win32.Interop;

namespace Cotabby.Win32.Focus;

/// <summary>
/// Reads whether the foreground app's input method is converting into its native script. Windows
/// keeps another process's composition string private, but every input method answers these
/// queries through its default window, which works across processes.
/// </summary>
internal static partial class ImeState
{
    private const uint WM_IME_CONTROL = 0x0283;
    private const int IMC_GETCONVERSIONMODE = 0x0001;
    private const int IMC_GETOPENSTATUS = 0x0005;
    private const int IME_CMODE_NATIVE = 0x0001;
    private const uint QueryTimeoutMs = 20;

    [StructLayout(LayoutKind.Sequential)]
    private struct GUITHREADINFO
    {
        public int cbSize;
        public int flags;
        public IntPtr hwndActive, hwndFocus, hwndCapture, hwndMenuOwner, hwndMoveSize, hwndCaret;
        public NativeMethods.RECT rcCaret;
    }

    /// <summary>The input method's language when it is open in native mode, otherwise 0.</summary>
    public static int ConvertingLanguage()
    {
        try
        {
            IntPtr foreground = NativeMethods.GetForegroundWindow();
            if (foreground == IntPtr.Zero) return 0;
            uint thread = NativeMethods.GetWindowThreadProcessId(foreground, out _);
            int language = (int)(NativeMethods.GetKeyboardLayout(thread).ToInt64() & 0xFFFF);
            // Only East Asian input methods compose. Other layouts report native mode too.
            // Checked first, so other layouts skip the two messages to the input method.
            if ((language & 0x3FF) is not (0x04 or 0x11 or 0x12)) return 0;

            var info = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
            IntPtr focus = GetGUIThreadInfo(thread, ref info) && info.hwndFocus != IntPtr.Zero ? info.hwndFocus : foreground;

            IntPtr imeWindow = ImmGetDefaultIMEWnd(focus);
            if (imeWindow == IntPtr.Zero) return 0;
            if (Query(imeWindow, IMC_GETOPENSTATUS) is not { } open || open == 0) return 0;
            if (Query(imeWindow, IMC_GETCONVERSIONMODE) is not { } mode || (mode & IME_CMODE_NATIVE) == 0) return 0;
            return language;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private static long? Query(IntPtr imeWindow, int command) =>
        NativeMethods.SendMessageTimeout(
            imeWindow, WM_IME_CONTROL, (IntPtr)command, IntPtr.Zero, NativeMethods.SMTO_ABORTIFHUNG, QueryTimeoutMs,
            out IntPtr result) == IntPtr.Zero
            ? null
            : result.ToInt64();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetGUIThreadInfo(uint threadId, ref GUITHREADINFO info);

    [LibraryImport("imm32.dll")]
    private static partial IntPtr ImmGetDefaultIMEWnd(IntPtr hwnd);
}
