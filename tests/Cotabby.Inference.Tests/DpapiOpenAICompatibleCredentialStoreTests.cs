using System.Text;
using Cotabby.Win32.Security;

namespace Cotabby.Inference.Tests;

public sealed class DpapiOpenAICompatibleCredentialStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"cotabby-tests-{Guid.NewGuid():N}");
    private readonly string _filePath;

    public DpapiOpenAICompatibleCredentialStoreTests() =>
        _filePath = Path.Combine(_directory, "nested", "endpoint-credential.bin");

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void SavedKeyRoundTripsThroughAnEncryptedFile()
    {
        var store = new DpapiOpenAICompatibleCredentialStore(_filePath);
        Assert.Null(store.ReadApiKey());

        store.SaveApiKey("  sk-secret-123\n");

        Assert.Equal("sk-secret-123", new DpapiOpenAICompatibleCredentialStore(_filePath).ReadApiKey());
        Assert.DoesNotContain("sk-secret-123", Encoding.Latin1.GetString(File.ReadAllBytes(_filePath)));

        store.SaveApiKey("replacement");
        Assert.Equal("replacement", store.ReadApiKey());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public void SavingABlankKeyDeletesTheStoredOne(string? blank)
    {
        var store = new DpapiOpenAICompatibleCredentialStore(_filePath);
        store.SaveApiKey("sk-secret-123");

        store.SaveApiKey(blank);

        Assert.False(File.Exists(_filePath));
        Assert.Null(store.ReadApiKey());
    }

    [Fact]
    public void DeleteIsSafeWhenNothingIsStored()
    {
        var store = new DpapiOpenAICompatibleCredentialStore(_filePath);

        store.DeleteApiKey();
        store.SaveApiKey("sk-secret-123");
        store.DeleteApiKey();
        store.DeleteApiKey();

        Assert.Null(store.ReadApiKey());
    }
}
