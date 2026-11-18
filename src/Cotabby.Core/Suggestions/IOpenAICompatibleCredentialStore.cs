namespace Cotabby.Core.Suggestions;

/// <summary>Narrow credential boundary so the app uses DPAPI while tests use an in-memory fake.</summary>
public interface IOpenAICompatibleCredentialStore
{
    string? ReadApiKey();

    /// <summary>Stores the trimmed key. A null or blank key deletes the stored one.</summary>
    void SaveApiKey(string? apiKey);

    void DeleteApiKey();
}
