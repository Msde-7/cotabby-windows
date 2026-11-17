using System.Runtime.InteropServices;
using static Cotabby.Win32.Interop.NativeMethods;

namespace Cotabby.Win32.Clipboard;

/// <summary>What the clipboard held before a paste, as raw bytes per format.</summary>
public sealed class ClipboardSnapshot
{
    internal ClipboardSnapshot(IReadOnlyList<(uint Format, byte[] Data)> formats) => Formats = formats;

    internal IReadOnlyList<(uint Format, byte[] Data)> Formats { get; }

    /// <summary>True when the clipboard held none of the formats worth keeping.</summary>
    public bool IsEmpty => Formats.Count == 0;
}

public enum ClipboardRestoreOutcome
{
    /// <summary>The saved content is back on the clipboard.</summary>
    Restored,

    /// <summary>Something else wrote to the clipboard after the paste, so it was left alone.</summary>
    ChangedSince,

    /// <summary>The clipboard could not be opened or written.</summary>
    Failed,
}

/// <summary>
/// Puts paste text on the clipboard and the user's content back afterwards, through the raw
/// Win32 clipboard API, which works from any thread and copies every kept format byte for byte.
/// </summary>
/// <remarks>
/// The save and the write happen while the clipboard is held open once, so nothing can change it
/// in between. Every write is marked so clipboard history, cloud clipboard and clipboard managers
/// skip it: the suggestion is not something the user copied, and the restored content is already
/// in their history.
/// </remarks>
public static class ClipboardSwap
{
    /// <summary>Largest snapshot taken. A bigger clipboard is not swapped, so it is never lost.</summary>
    public const long MaxSnapshotBytes = 64L * 1024 * 1024;

    private const int OpenAttempts = 10;
    private const int OpenRetryMs = 10;

    private static readonly Lazy<IReadOnlyDictionary<string, uint>> FormatIds = new(() =>
        ClipboardFormatPolicy.Candidates.ToDictionary(name => name, name => name switch
        {
            ClipboardFormatPolicy.UnicodeText => CF_UNICODETEXT,
            ClipboardFormatPolicy.Text => CF_TEXT,
            ClipboardFormatPolicy.FileDrop => CF_HDROP,
            ClipboardFormatPolicy.Dib => CF_DIB,
            _ => RegisterClipboardFormat(name),
        }));

    private static readonly Lazy<uint[]> PrivacyFormats = new(() =>
    [
        RegisterClipboardFormat("ExcludeClipboardContentFromMonitorProcessing"),
        RegisterClipboardFormat("CanIncludeInClipboardHistory"),
        RegisterClipboardFormat("CanUploadToCloudClipboard"),
    ]);

    /// <summary>
    /// Saves the clipboard and replaces it with <paramref name="text"/>. False when the clipboard
    /// could not be read or written, in which case it is left as it was and nothing may be pasted.
    /// </summary>
    /// <param name="sequence">The clipboard sequence number right after the write.</param>
    public static bool TryReplaceWithText(string text, out ClipboardSnapshot? saved, out uint sequence)
    {
        saved = null;
        sequence = 0;
        if (!TryOpen()) return false;
        try
        {
            if (Capture() is not { } snapshot || !EmptyClipboard()) return false;
            if (!Put(CF_UNICODETEXT, TextBytes(text)))
            {
                // The clipboard is already empty, so the user's content goes straight back.
                PutAll(snapshot);
                return false;
            }
            PutPrivacyMarkers();
            saved = snapshot;
        }
        finally
        {
            CloseClipboard();
        }
        sequence = GetClipboardSequenceNumber();
        return true;
    }

    /// <summary>
    /// Puts <paramref name="saved"/> back, unless the clipboard changed since
    /// <paramref name="expectedSequence"/>, which means the user or another app copied something
    /// that must not be overwritten.
    /// </summary>
    public static ClipboardRestoreOutcome TryRestore(ClipboardSnapshot saved, uint expectedSequence)
    {
        if (!TryOpen()) return ClipboardRestoreOutcome.Failed;
        try
        {
            if (GetClipboardSequenceNumber() != expectedSequence) return ClipboardRestoreOutcome.ChangedSince;
            if (!EmptyClipboard()) return ClipboardRestoreOutcome.Failed;
            if (saved.IsEmpty) return ClipboardRestoreOutcome.Restored;
            bool all = PutAll(saved);
            PutPrivacyMarkers();
            return all ? ClipboardRestoreOutcome.Restored : ClipboardRestoreOutcome.Failed;
        }
        finally
        {
            CloseClipboard();
        }
    }

    /// <summary>
    /// Copies the kept formats out of the open clipboard. Null when one of them could not be read
    /// or they add up past <see cref="MaxSnapshotBytes"/>.
    /// </summary>
    private static ClipboardSnapshot? Capture()
    {
        var ids = FormatIds.Value;
        var available = ClipboardFormatPolicy.Candidates.Where(name => IsClipboardFormatAvailable(ids[name])).ToList();
        var formats = new List<(uint, byte[])>();
        long total = 0;
        foreach (var name in ClipboardFormatPolicy.Keep(available))
        {
            uint id = ids[name];
            IntPtr handle = GetClipboardData(id);
            if (handle == IntPtr.Zero) return null;
            long size = (long)GlobalSize(handle);
            total += size;
            if (total > MaxSnapshotBytes) return null;
            IntPtr data = GlobalLock(handle);
            if (data == IntPtr.Zero) return null;
            try
            {
                var bytes = new byte[size];
                Marshal.Copy(data, bytes, 0, bytes.Length);
                formats.Add((id, bytes));
            }
            finally
            {
                GlobalUnlock(handle);
            }
        }
        return new ClipboardSnapshot(formats);
    }

    private static bool PutAll(ClipboardSnapshot snapshot)
    {
        bool all = true;
        foreach (var (format, data) in snapshot.Formats) all &= Put(format, data);
        return all;
    }

    private static void PutPrivacyMarkers()
    {
        var zero = new byte[sizeof(uint)];
        foreach (uint format in PrivacyFormats.Value) Put(format, zero);
    }

    private static byte[] TextBytes(string text)
    {
        var bytes = new byte[(text.Length + 1) * sizeof(char)];
        MemoryMarshal.AsBytes(text.AsSpan()).CopyTo(bytes);
        return bytes;
    }

    /// <summary>Hands one format to the open clipboard, which owns the memory once it succeeds.</summary>
    private static bool Put(uint format, byte[] data)
    {
        if (format == 0) return false;
        IntPtr handle = GlobalAlloc(GMEM_MOVEABLE, (nuint)Math.Max(data.Length, 1));
        if (handle == IntPtr.Zero) return false;
        IntPtr target = GlobalLock(handle);
        if (target == IntPtr.Zero)
        {
            GlobalFree(handle);
            return false;
        }
        Marshal.Copy(data, 0, target, data.Length);
        GlobalUnlock(handle);
        if (SetClipboardData(format, handle) != IntPtr.Zero) return true;
        GlobalFree(handle);
        return false;
    }

    /// <summary>Another process may hold the clipboard open for a moment.</summary>
    private static bool TryOpen()
    {
        for (int attempt = 1; attempt <= OpenAttempts; attempt++)
        {
            if (OpenClipboard(IntPtr.Zero)) return true;
            if (attempt < OpenAttempts) Thread.Sleep(OpenRetryMs);
        }
        return false;
    }
}
