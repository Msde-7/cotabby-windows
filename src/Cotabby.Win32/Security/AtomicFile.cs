using System.IO;

namespace Cotabby.Win32.Security;

/// <summary>
/// Replaces a file in one step: the bytes go to a temporary file beside it, are flushed to the
/// disk and the temporary file is then moved over the old one. A crash or power cut mid-write
/// leaves either the old file or the new one, never a torn mix.
/// </summary>
public static class AtomicFile
{
    public static void WriteAllBytes(string path, ReadOnlySpan<byte> bytes)
    {
        if (Path.GetDirectoryName(path) is { Length: > 0 } directory) Directory.CreateDirectory(directory);
        string temporary = path + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, path, overwrite: true);
    }
}
