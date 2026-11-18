using System.Runtime.InteropServices;
using static Cotabby.Win32.Interop.NativeMethods;

namespace Cotabby.Win32.Clipboard;

/// <summary>What the clipboard held before a paste, as raw bytes per format.</summary>
public sealed class ClipboardSnapshot
{
    internal ClipboardSnapshot(IReadOnlyList<(ClipboardSaveStep Step, byte[] Data)> formats, bool isPrivate)
    {
        Formats = formats;
        IsPrivate = isPrivate;
    }

    internal IReadOnlyList<(ClipboardSaveStep Step, byte[] Data)> Formats { get; }

    /// <summary>True when the clipboard was empty.</summary>
    public bool IsEmpty => Formats.Count == 0;

    /// <summary>True when the app that copied the content asked clipboard tools to leave it alone.</summary>
    public bool IsPrivate { get; }
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

    private static readonly Lazy<uint> ExcludeFromMonitoringFormat =
        new(() => RegisterClipboardFormat("ExcludeClipboardContentFromMonitorProcessing"));

    private static readonly Lazy<uint> CanIncludeInHistoryFormat =
        new(() => RegisterClipboardFormat("CanIncludeInClipboardHistory"));

    private static readonly Lazy<uint> ViewerIgnoreFormat = new(() => RegisterClipboardFormat("Clipboard Viewer Ignore"));

    private static readonly Lazy<uint[]> PrivacyFormats = new(() =>
    [
        ExcludeFromMonitoringFormat.Value,
        CanIncludeInHistoryFormat.Value,
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
        if (!TryOpen(OpenAttempts, OpenRetryMs)) return false;
        try
        {
            if (Capture() is not { } snapshot || !EmptyClipboard()) return false;
            // Classic edit controls show a bare line feed as no break at all.
            if (!Put(CF_UNICODETEXT, TextBytes(text.ReplaceLineEndings("\r\n"))))
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
    /// <param name="sequence">The clipboard sequence number right after a restore, otherwise 0.</param>
    public static ClipboardRestoreOutcome TryRestore(ClipboardSnapshot saved, uint expectedSequence, out uint sequence)
    {
        sequence = 0;
        if (!TryOpen(OpenAttempts, OpenRetryMs)) return ClipboardRestoreOutcome.Failed;
        var outcome = ClipboardRestoreOutcome.Failed;
        try
        {
            if (GetClipboardSequenceNumber() != expectedSequence) return ClipboardRestoreOutcome.ChangedSince;
            if (!EmptyClipboard()) return ClipboardRestoreOutcome.Failed;
            if (saved.IsEmpty)
            {
                outcome = ClipboardRestoreOutcome.Restored;
            }
            else
            {
                bool all = PutAll(saved);
                PutPrivacyMarkers();
                if (all) outcome = ClipboardRestoreOutcome.Restored;
            }
        }
        finally
        {
            CloseClipboard();
        }
        if (outcome == ClipboardRestoreOutcome.Restored) sequence = GetClipboardSequenceNumber();
        return outcome;
    }

    /// <summary>
    /// True when the open clipboard holds content its app asked clipboard tools to leave alone, as
    /// password managers do for a copied secret.
    /// </summary>
    internal static bool HoldsPrivateContent()
    {
        if (IsClipboardFormatAvailable(ExcludeFromMonitoringFormat.Value)) return true;
        if (IsClipboardFormatAvailable(ViewerIgnoreFormat.Value)) return true;
        uint canInclude = CanIncludeInHistoryFormat.Value;
        if (!IsClipboardFormatAvailable(canInclude)) return false;
        return ClipboardFormatPolicy.IsPrivate(false, false, ReadBytes(GetClipboardData(canInclude), sizeof(uint) * 16) ?? []);
    }

    /// <summary>
    /// Copies every format out of the open clipboard. Null when one of them cannot be saved
    /// faithfully or read, or they add up past <see cref="MaxSnapshotBytes"/>.
    /// </summary>
    private static ClipboardSnapshot? Capture()
    {
        var available = new List<uint>();
        for (uint format = EnumClipboardFormats(0); format != 0; format = EnumClipboardFormats(format)) available.Add(format);
        if (ClipboardFormatPolicy.Plan(available) is not { } plan) return null;

        var formats = new List<(ClipboardSaveStep, byte[])>();
        long total = 0;
        foreach (var step in plan)
        {
            IntPtr handle = GetClipboardData(step.Format);
            byte[]? bytes = step.Kind == ClipboardSaveKind.EnhancedMetafile
                ? ReadMetafile(handle, MaxSnapshotBytes - total)
                : ReadBytes(handle, MaxSnapshotBytes - total);
            if (bytes is null) return null;
            total += bytes.Length;
            formats.Add((step, bytes));
        }

        byte[]? canInclude = formats.FirstOrDefault(saved => saved.Item1.Format == CanIncludeInHistoryFormat.Value).Item2;
        bool isPrivate = ClipboardFormatPolicy.IsPrivate(
            available.Contains(ExcludeFromMonitoringFormat.Value), available.Contains(ViewerIgnoreFormat.Value), canInclude);
        return new ClipboardSnapshot(formats, isPrivate);
    }

    /// <summary>A memory block's bytes, or null when it cannot be read or is larger than <paramref name="limit"/>.</summary>
    private static byte[]? ReadBytes(IntPtr handle, long limit)
    {
        if (handle == IntPtr.Zero) return null;
        long size = (long)GlobalSize(handle);
        if (size > limit) return null;
        IntPtr data = GlobalLock(handle);
        if (data == IntPtr.Zero) return null;
        try
        {
            var bytes = new byte[size];
            Marshal.Copy(data, bytes, 0, bytes.Length);
            return bytes;
        }
        finally
        {
            GlobalUnlock(handle);
        }
    }

    private static byte[]? ReadMetafile(IntPtr handle, long limit)
    {
        if (handle == IntPtr.Zero) return null;
        uint size = GetEnhMetaFileBits(handle, 0, []);
        if (size == 0 || size > limit) return null;
        var bytes = new byte[size];
        return GetEnhMetaFileBits(handle, size, bytes) == size ? bytes : null;
    }

    private static bool PutAll(ClipboardSnapshot snapshot)
    {
        bool all = true;
        foreach (var (step, data) in snapshot.Formats)
        {
            all &= step.Kind == ClipboardSaveKind.EnhancedMetafile ? PutMetafile(data) : Put(step.Format, data);
        }
        return all;
    }

    /// <summary>Hands an enhanced metafile to the open clipboard, which owns it once it succeeds.</summary>
    private static bool PutMetafile(byte[] data)
    {
        IntPtr metafile = SetEnhMetaFileBits((uint)data.Length, data);
        if (metafile == IntPtr.Zero) return false;
        if (SetClipboardData(ClipboardFormatPolicy.CF_ENHMETAFILE, metafile) != IntPtr.Zero) return true;
        DeleteEnhMetaFile(metafile);
        return false;
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
    internal static bool TryOpen(int attempts, int retryMs)
    {
        for (int attempt = 1; attempt <= attempts; attempt++)
        {
            if (OpenClipboard(IntPtr.Zero)) return true;
            if (attempt < attempts) Thread.Sleep(retryMs);
        }
        return false;
    }
}
