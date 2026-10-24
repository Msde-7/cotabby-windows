using Cotabby.Core.History;
using Cotabby.Win32.Security;

namespace Cotabby.Inference.Tests;

public sealed class DpapiTypingHistoryKeyStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"cotabby-tests-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private string KeyPath => Path.Combine(_directory, "typing-history.key");

    [Fact]
    public void CreatedKeyIsProtectedAndReadBack()
    {
        var store = new DpapiTypingHistoryKeyStore(KeyPath);
        Assert.Null(store.ExistingKey());

        byte[] key = store.CreateKey();

        Assert.Equal(32, key.Length);
        Assert.Equal(key, new DpapiTypingHistoryKeyStore(KeyPath).ExistingKey());
        Assert.NotEqual(key, File.ReadAllBytes(KeyPath));
    }

    [Fact]
    public void ArchiveSealedWithTheKeyReopensAndDestroyRemovesBoth()
    {
        var vault = new TypingHistoryVault(Path.Combine(_directory, "TypingHistory.sealed"), new DpapiTypingHistoryKeyStore(KeyPath));
        var record = new TypingHistoryRecord
        {
            Id = Guid.NewGuid(),
            BundleIdentifier = "notepad",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Text = "A sentence that stays on this PC.",
            Source = TypingHistorySource.Recorded,
            TypedLength = 10,
        };

        vault.Save([record]);
        Assert.Equal([record], vault.Load());

        vault.Destroy();
        Assert.False(File.Exists(vault.FilePath));
        Assert.False(File.Exists(KeyPath));
        Assert.Empty(vault.Load());
    }

    [Fact]
    public void AnUnreadableKeyFileIsAnErrorNotAMissingKey()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllBytes(KeyPath, [1, 2, 3]);

        Assert.ThrowsAny<Exception>(() => new DpapiTypingHistoryKeyStore(KeyPath).ExistingKey());
    }
}
