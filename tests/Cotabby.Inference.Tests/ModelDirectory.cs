using Cotabby.Core.Models;

namespace Cotabby.Inference.Tests;

/// <summary>Tests that redirect the models folder share one collection, because the override is process wide.</summary>
[CollectionDefinition(Name)]
public sealed class ModelDirectoryCollection
{
    public const string Name = "Models folder";
}

/// <summary>Points the models folder at a temporary directory for one test.</summary>
public sealed class ModelDirectory : IDisposable
{
    private readonly string? _previous = Environment.GetEnvironmentVariable(ModelCatalog.DirectoryOverrideVariable);

    public string Path { get; } =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cotabby-tests-" + Guid.NewGuid().ToString("N"));

    public ModelDirectory() => Environment.SetEnvironmentVariable(ModelCatalog.DirectoryOverrideVariable, Path);

    public void Write(string fileName, byte[]? bytes = null)
    {
        Directory.CreateDirectory(Path);
        File.WriteAllBytes(System.IO.Path.Combine(Path, fileName), bytes ?? [.. "GGUF"u8, 0, 0, 0, 3]);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(ModelCatalog.DirectoryOverrideVariable, _previous);
        if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
    }
}
