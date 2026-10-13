using Cotabby.Core.Context;
using static Cotabby.Win32.Interop.NativeMethods;

namespace Cotabby.Win32.Clipboard;

/// <summary>
/// Reads clipboard text through the raw Win32 API, which works from any thread.
/// </summary>
/// <remarks>
/// The inserter pastes long suggestions through the clipboard and then puts the old contents
/// back. It brackets that with <see cref="BeginOwnWrite"/> and <see cref="EndOwnWrite"/>, and the
/// changes made in between are subtracted from <see cref="SequenceNumber"/>. A paste therefore
/// never looks like a copy by the user.
/// </remarks>
public sealed class ClipboardTextReader : IClipboardReader
{
    /// <summary>Longest text returned. Anything past it is cut off.</summary>
    public const int MaxChars = 16_384;

    private const int OpenAttempts = 3;
    private const int OpenRetryMs = 5;

    private readonly object _gate = new();
    private uint _ownChanges;
    private uint? _ownWriteStart;
    private uint? _ownTextSequence;

    public uint SequenceNumber
    {
        get
        {
            lock (_gate) return unchecked((_ownWriteStart ?? GetClipboardSequenceNumber()) - _ownChanges);
        }
    }

    public string? ReadText()
    {
        lock (_gate)
        {
            if (_ownWriteStart is not null || GetClipboardSequenceNumber() == _ownTextSequence) return null;
            return ReadUnicodeText();
        }
    }

    /// <summary>Call before Cotabby writes to the clipboard. Until the matching end the clipboard reads as empty.</summary>
    public void BeginOwnWrite()
    {
        lock (_gate) _ownWriteStart = GetClipboardSequenceNumber();
    }

    /// <param name="restored">False when Cotabby's own text was left on the clipboard.</param>
    public void EndOwnWrite(bool restored)
    {
        lock (_gate)
        {
            if (_ownWriteStart is not { } start) return;
            uint now = GetClipboardSequenceNumber();
            _ownChanges = unchecked(_ownChanges + now - start);
            _ownTextSequence = restored || now == start ? null : now;
            _ownWriteStart = null;
        }
    }

    private static unsafe string? ReadUnicodeText()
    {
        if (!TryOpenClipboard()) return null;
        try
        {
            IntPtr handle = GetClipboardData(CF_UNICODETEXT);
            if (handle == IntPtr.Zero) return null;
            char* text = (char*)GlobalLock(handle);
            if (text is null) return null;
            try
            {
                int limit = (int)Math.Min(GlobalSize(handle) / sizeof(char), (nuint)MaxChars);
                int length = new ReadOnlySpan<char>(text, limit).IndexOf('\0');
                if (length < 0) length = limit;
                // A cut must not leave half a surrogate pair.
                if (length > 0 && char.IsHighSurrogate(text[length - 1])) length--;
                return new string(text, 0, length);
            }
            finally
            {
                GlobalUnlock(handle);
            }
        }
        finally
        {
            CloseClipboard();
        }
    }

    /// <summary>Another process may hold the clipboard open for a moment.</summary>
    private static bool TryOpenClipboard()
    {
        for (int attempt = 1; attempt <= OpenAttempts; attempt++)
        {
            if (OpenClipboard(IntPtr.Zero)) return true;
            if (attempt < OpenAttempts) Thread.Sleep(OpenRetryMs);
        }
        return false;
    }
}
