using System.IO;
using System.Security.Cryptography;
using System.Text;
using Cotabby.Inference.OpenAICompatible;

namespace Cotabby.Win32.Security;

/// <summary>
/// Stores the optional endpoint bearer token in a file encrypted with DPAPI for the current
/// Windows user. The token never reaches the settings file or the logs.
/// </summary>
public sealed class DpapiOpenAICompatibleCredentialStore(string? filePath = null) : IOpenAICompatibleCredentialStore
{
    private readonly string _filePath = filePath ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Cotabby",
        "endpoint-credential.bin");

    public string? ReadApiKey()
    {
        if (!File.Exists(_filePath)) return null;
        var plain = ProtectedData.Unprotect(File.ReadAllBytes(_filePath), null, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(plain);
    }

    public void SaveApiKey(string? apiKey)
    {
        if (apiKey?.Trim() is not { Length: > 0 } key)
        {
            DeleteApiKey();
            return;
        }

        if (Path.GetDirectoryName(_filePath) is { Length: > 0 } directory)
        {
            Directory.CreateDirectory(directory);
        }
        File.WriteAllBytes(
            _filePath,
            ProtectedData.Protect(Encoding.UTF8.GetBytes(key), null, DataProtectionScope.CurrentUser));
    }

    public void DeleteApiKey()
    {
        if (File.Exists(_filePath)) File.Delete(_filePath);
    }
}
