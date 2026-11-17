using System.IO;
using System.Security.Cryptography;
using Cotabby.Core.History;

namespace Cotabby.Win32.Security;

/// <summary>
/// Keeps the typing history key in a file protected with DPAPI for the current Windows user, so
/// only this user on this PC can open the archive. The archive itself is sealed with AES-GCM by
/// <see cref="TypingHistoryVault"/>.
/// </summary>
public sealed class DpapiTypingHistoryKeyStore(string? filePath = null) : ITypingHistoryKeyStore
{
    private const int KeySize = 32;
    private static readonly byte[] Entropy = "Cotabby typing history"u8.ToArray();

    private readonly string _filePath = filePath ?? Path.Combine(DefaultDirectory, "typing-history.key");

    /// <summary>%LOCALAPPDATA%\Cotabby. Local rather than roaming, so the history never syncs.</summary>
    public static string DefaultDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Cotabby");

    /// <summary>The production vault: TypingHistory.sealed beside its key.</summary>
    public static TypingHistoryVault StandardVault() =>
        new(Path.Combine(DefaultDirectory, "TypingHistory.sealed"), new DpapiTypingHistoryKeyStore());

    public byte[]? ExistingKey()
    {
        if (!File.Exists(_filePath)) return null;
        byte[] key = ProtectedData.Unprotect(File.ReadAllBytes(_filePath), Entropy, DataProtectionScope.CurrentUser);
        return key.Length == KeySize
            ? key
            : throw new TypingHistoryVaultException("The typing history key has the wrong size.");
    }

    public byte[] CreateKey()
    {
        byte[] key = RandomNumberGenerator.GetBytes(KeySize);
        AtomicFile.WriteAllBytes(_filePath, ProtectedData.Protect(key, Entropy, DataProtectionScope.CurrentUser));
        return key;
    }

    public void DeleteKey()
    {
        if (File.Exists(_filePath)) File.Delete(_filePath);
    }
}
