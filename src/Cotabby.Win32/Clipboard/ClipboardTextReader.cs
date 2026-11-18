using Cotabby.Core.Context;
using static Cotabby.Win32.Interop.NativeMethods;

namespace Cotabby.Win32.Clipboard;

/// <summary>
/// Reads clipboard text through the raw Win32 API, which works from any thread.
/// </summary>
/// <remarks>
/// The inserter pastes long suggestions through the clipboard and then puts the old content back.
/// It brackets that with <see cref="BeginOwnWrite"/> and <see cref="EndOwnWrite"/>, and the
/// changes made in between are subtracted from <see cref="SequenceNumber"/>. A paste therefore
/// never looks like a copy by the user.
/// <para>
/// Content its app asked clipboard tools to leave alone, such as a secret copied from a password
/// manager, is never read, so it cannot reach a prompt. Cotabby marks the content it puts back the
/// same way to keep it out of clipboard history, so that content is judged by what it was before.
/// </para>
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
    private RestoredClipboard? _restored;

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
            uint now = GetClipboardSequenceNumber();
            if (_ownWriteStart is not null || now == _ownTextSequence) return null;
            bool restored = now == _restored?.Sequence;
            if (restored && _restored!.Value.IsPrivate) return null;
            return ReadUnicodeText(skipPrivate: !restored);
        }
    }

    /// <summary>Call before Cotabby writes to the clipboard. Until the matching end the clipboard reads as empty.</summary>
    public void BeginOwnWrite()
    {
        lock (_gate) _ownWriteStart = GetClipboardSequenceNumber();
    }

    /// <param name="restored">False when Cotabby's own text was left on the clipboard.</param>
    /// <param name="restoredSequence">The sequence number right after the user's content went back, if it did.</param>
    /// <param name="restoredIsPrivate">The content that went back was private before Cotabby saved it.</param>
    public void EndOwnWrite(bool restored, uint? restoredSequence = null, bool restoredIsPrivate = false)
    {
        lock (_gate)
        {
            if (_ownWriteStart is not { } start) return;
            uint now = GetClipboardSequenceNumber();
            _ownChanges = unchecked(_ownChanges + now - start);
            _ownTextSequence = restored || now == start ? null : now;
            _ownWriteStart = null;
            _restored = RestoredClipboard.After(
                _restored, restoredSequence is { } sequence ? new RestoredClipboard(sequence, restoredIsPrivate) : null, now);
        }
    }

    /// <param name="skipPrivate">Returns null for content marked private.</param>
    private static unsafe string? ReadUnicodeText(bool skipPrivate)
    {
        if (!ClipboardSwap.TryOpen(OpenAttempts, OpenRetryMs)) return null;
        try
        {
            if (skipPrivate && ClipboardSwap.HoldsPrivateContent()) return null;
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
}
