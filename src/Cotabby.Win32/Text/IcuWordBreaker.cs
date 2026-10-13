using System.Runtime.InteropServices;
using Cotabby.Core.Suggestions;

namespace Cotabby.Win32.Text;

/// <summary>
/// Word segmentation for space-less scripts through the ICU that ships with
/// Windows 10 1903 and later. Falls back to the script-run heuristic when ICU is missing.
/// </summary>
public static partial class IcuWordBreaker
{
    private const int UBRK_WORD = 1;
    private const int UBRK_DONE = -1;

    private static bool _unavailable;

    /// <summary>Routes acceptance chunking of space-less scripts through ICU.</summary>
    public static void Install() => AcceptanceChunker.SpacelessWordEnd = FirstWordEnd;

    public static unsafe int? FirstWordEnd(string text, int start, int limit)
    {
        if (_unavailable) return AcceptanceChunker.ScriptRunWordEnd(text, start, limit);
        try
        {
            // ICU reads the buffer on every call, so it stays pinned until the breaker closes.
            fixed (char* chars = text)
            {
                int status = 0;
                IntPtr breaker = ubrk_open(UBRK_WORD, null, chars + start, limit - start, &status);
                if (status > 0 || breaker == IntPtr.Zero) return AcceptanceChunker.ScriptRunWordEnd(text, start, limit);
                int end = ubrk_next(breaker);
                ubrk_close(breaker);
                return end is UBRK_DONE or <= 0 ? null : start + end;
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            _unavailable = true;
            return AcceptanceChunker.ScriptRunWordEnd(text, start, limit);
        }
    }

    [LibraryImport("icu.dll")]
    private static unsafe partial IntPtr ubrk_open(int type, byte* locale, char* text, int textLength, int* status);

    [LibraryImport("icu.dll")]
    private static partial int ubrk_next(IntPtr breaker);

    [LibraryImport("icu.dll")]
    private static partial void ubrk_close(IntPtr breaker);
}
