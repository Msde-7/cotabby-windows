using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cotabby.Core.History;

/// <summary>The archive exists but cannot be opened, or its key store failed.</summary>
public sealed class TypingHistoryVaultException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Where the vault's 256-bit key lives. An interface so tests keep keys in memory instead of
/// writing them into the user's profile.
/// </summary>
public interface ITypingHistoryKeyStore
{
    /// <summary>The stored key, or null when none was created. Throws when it cannot be read.</summary>
    byte[]? ExistingKey();

    byte[] CreateKey();

    void DeleteKey();
}

/// <summary>
/// Encrypted on-disk storage for typing history.
/// <para>
/// The archive holds months of what the user wrote. Sealing it with AES-GCM under a random key
/// that only the user's key store can open means a copied file or a backup is ciphertext. Errors
/// are surfaced rather than swallowed: a failed decrypt must not be mistaken for "no history" and
/// then overwritten. The vault holds no mutable state, so it can be used from any thread.
/// </para>
/// </summary>
public sealed class TypingHistoryVault(string filePath, ITypingHistoryKeyStore keyStore)
{
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public string FilePath => filePath;

    /// <summary>The stored records, or an empty list when nothing has been saved yet.</summary>
    public List<TypingHistoryRecord> Load()
    {
        if (!File.Exists(filePath)) return [];
        // A file without its key can never be opened again. Report it instead of returning an
        // empty list, so the caller does not overwrite it as if it were empty.
        var key = keyStore.ExistingKey() ?? throw new TypingHistoryVaultException("The typing history key is missing.");
        byte[] sealedBytes = File.ReadAllBytes(filePath);
        try
        {
            if (sealedBytes.Length < NonceSize + TagSize) throw new CryptographicException("The archive is truncated.");
            var nonce = sealedBytes.AsSpan(0, NonceSize);
            var cipher = sealedBytes.AsSpan(NonceSize, sealedBytes.Length - NonceSize - TagSize);
            var tag = sealedBytes.AsSpan(sealedBytes.Length - TagSize);
            byte[] plain = new byte[cipher.Length];
            using (var aes = new AesGcm(key, TagSize)) aes.Decrypt(nonce, cipher, tag, plain);
            var archive = JsonSerializer.Deserialize<TypingHistoryArchive>(plain, JsonOptions)
                ?? throw new JsonException("The archive is empty.");
            return archive.Records;
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or ArgumentException)
        {
            throw new TypingHistoryVaultException("The typing history archive is corrupt.", ex);
        }
    }

    /// <summary>Seals the records as nonce, ciphertext and tag, and replaces the file atomically.</summary>
    public void Save(IReadOnlyList<TypingHistoryRecord> records)
    {
        var key = keyStore.ExistingKey() ?? keyStore.CreateKey();
        byte[] plain = JsonSerializer.SerializeToUtf8Bytes(
            new TypingHistoryArchive { Records = [.. records] }, JsonOptions);
        byte[] sealedBytes = new byte[NonceSize + plain.Length + TagSize];
        var nonce = sealedBytes.AsSpan(0, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using (var aes = new AesGcm(key, TagSize))
        {
            aes.Encrypt(nonce, plain, sealedBytes.AsSpan(NonceSize, plain.Length), sealedBytes.AsSpan(NonceSize + plain.Length));
        }

        if (Path.GetDirectoryName(filePath) is { Length: > 0 } directory) Directory.CreateDirectory(directory);
        string temporary = filePath + ".tmp";
        // The bytes reach the disk before the rename. Otherwise a power loss right after the move
        // can leave the archive name pointing at a file whose contents were never written.
        using (var file = new FileStream(
            temporary, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 4096, FileOptions.WriteThrough))
        {
            file.Write(sealedBytes);
            file.Flush(flushToDisk: true);
        }
        File.Move(temporary, filePath, overwrite: true);
    }

    /// <summary>
    /// Deletes the archive and then its key. Without the key, even a copy of the file left in a
    /// backup can no longer be decrypted.
    /// </summary>
    public void Destroy()
    {
        if (File.Exists(filePath)) File.Delete(filePath);
        keyStore.DeleteKey();
    }
}

/// <summary>
/// Serializes every write and delete of the archive, and drops writes a deletion made stale.
/// <para>
/// Saves run in the background with a snapshot of the records. Cancelling a save that already
/// started does not stop it, so without this a save begun just before Delete All could finish
/// afterwards and write the deleted history back. Every operation takes one lock, so a delete waits
/// for an in-flight save, and each delete raises the minimum generation so earlier saves are
/// skipped. A lock rather than async work because the exit flush writes synchronously.
/// </para>
/// </summary>
public sealed class TypingHistoryWriter(TypingHistoryVault vault)
{
    private readonly Lock _lock = new();

    /// <summary>Writes captured at an older generation than this are stale.</summary>
    private int _minimumGeneration;

    /// <summary>An older snapshot finishing late cannot overwrite a newer one already on disk.</summary>
    private int _lastWrittenSequence;

    /// <summary>Writes the records unless a deletion or a newer write has made them stale.</summary>
    public void Save(IReadOnlyList<TypingHistoryRecord> records, int generation, int sequence)
    {
        lock (_lock)
        {
            if (generation < _minimumGeneration || sequence <= _lastWrittenSequence) return;
            vault.Save(records);
            _lastWrittenSequence = sequence;
        }
    }

    /// <summary>Deletes the archive and its key after any write in progress, and marks earlier writes stale.</summary>
    public void Destroy(int generation)
    {
        lock (_lock)
        {
            _minimumGeneration = Math.Max(_minimumGeneration, generation);
            vault.Destroy();
        }
    }
}
