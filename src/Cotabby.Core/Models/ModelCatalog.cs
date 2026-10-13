namespace Cotabby.Core.Models;

/// <summary>
/// A model the app can run. Built-in models carry a download URL. A model the
/// user dropped into the models folder has none.
/// </summary>
public sealed record CotabbyModel
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }

    /// <summary>Short human description shown in pickers ("fastest", "recommended").</summary>
    public string Subtitle { get; init; } = "";

    /// <summary>Hugging Face URL of the .gguf file, or empty for a model the user supplied.</summary>
    public required string DownloadUrl { get; init; }

    public bool IsCustom => DownloadUrl.Length == 0;

    /// <summary>Size of the served file, shown before a download starts.</summary>
    public required long ApproxSizeBytes { get; init; }

    /// <summary>Context window to allocate at load time (token count).</summary>
    public required int ContextLength { get; init; }

    /// <summary>True if the model expects a fill-in-middle prompt template.</summary>
    public required bool SupportsFillInMiddle { get; init; }

    /// <summary>The file name under the models directory. Built-in models take it from their URL.</summary>
    public string FileName
    {
        get => _fileName ?? Path.GetFileName(new Uri(DownloadUrl).AbsolutePath);
        init => _fileName = value;
    }

    private readonly string? _fileName;
}

/// <summary>
/// Built-in model list. Treated as code rather than config because the
/// number of models is small and the prompting strategy for each is
/// bound to its tokenizer.
/// </summary>
public static class ModelCatalog
{
    private static string HfUrl(string repo, string file) =>
        $"https://huggingface.co/{repo}/resolve/main/{file}?download=true";

    public static IReadOnlyList<CotabbyModel> All { get; } =
    [
        // The same four *base* checkpoints upstream Cotabby ships
        // (RuntimeModelCatalog.downloadableModels): base models continue text
        // instead of answering it, which is the entire premise of the
        // BaseCompletionPromptRenderer pipeline. The instruct-tuned
        // Qwen2.5-Coder GGUFs this port used previously treated the user's
        // prose as a chat turn and replied as an assistant — every
        // "chat-leak" and degeneration guard in the git history was fighting
        // that mismatch. ApproxSizeBytes are exact Content-Length values
        // HEAD'd from the mradermacher repos (no auth required).
        new CotabbyModel
        {
            Id = "tabby-2-mini",
            DisplayName = "tabby-2-mini",
            Subtitle = "Qwen3.5 2B Base · 1.3 GB — recommended",
            DownloadUrl = HfUrl("mradermacher/Qwen3.5-2B-Base-i1-GGUF", "Qwen3.5-2B-Base.i1-Q4_K_M.gguf"),
            ApproxSizeBytes = 1_274_397_056,
            ContextLength = 4096,
            SupportsFillInMiddle = true,
        },
        new CotabbyModel
        {
            Id = "tabby-2-nano",
            DisplayName = "tabby-2-nano",
            Subtitle = "Qwen3.5 0.8B Base · 0.6 GB — fastest, low-memory",
            DownloadUrl = HfUrl("mradermacher/Qwen3.5-0.8B-Base-i1-GGUF", "Qwen3.5-0.8B-Base.i1-Q6_K.gguf"),
            ApproxSizeBytes = 629_744_512,
            ContextLength = 4096,
            SupportsFillInMiddle = true,
        },
        new CotabbyModel
        {
            Id = "tabby-2-base",
            DisplayName = "tabby-2-base",
            Subtitle = "Gemma 4 E2B · 3.9 GB — higher quality",
            DownloadUrl = HfUrl("mradermacher/gemma-4-E2B-i1-GGUF", "gemma-4-E2B.i1-Q6_K.gguf"),
            ApproxSizeBytes = 3_845_328_608,
            ContextLength = 4096,
            SupportsFillInMiddle = false,
        },
        new CotabbyModel
        {
            Id = "tabby-2-pro",
            DisplayName = "tabby-2-pro",
            Subtitle = "Gemma 4 E4B · 5.3 GB — best quality, needs 16 GB+ RAM",
            DownloadUrl = HfUrl("mradermacher/gemma-4-E4B-i1-GGUF", "gemma-4-E4B.i1-Q4_K_M.gguf"),
            ApproxSizeBytes = 5_335_274_240,
            ContextLength = 4096,
            SupportsFillInMiddle = false,
        },
    ];

    private const string CustomIdPrefix = "custom:";

    /// <summary>The built-in models plus every other GGUF file in the models folder.</summary>
    public static IReadOnlyList<CotabbyModel> Available()
    {
        var directory = DefaultLocalDirectory();
        if (!Directory.Exists(directory)) return All;

        var builtIn = All.Select(m => m.FileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var custom = Directory.EnumerateFiles(directory, "*.gguf")
            .Where(path => !builtIn.Contains(Path.GetFileName(path)))
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .Select(Custom);
        return [.. All, .. custom];
    }

    public static CotabbyModel? FindById(string id) =>
        id.StartsWith(CustomIdPrefix, StringComparison.Ordinal)
            ? Available().FirstOrDefault(m => m.Id == id)
            : All.FirstOrDefault(m => m.Id == id);

    private static CotabbyModel Custom(string path)
    {
        long bytes = new FileInfo(path).Length;
        return new CotabbyModel
        {
            Id = CustomIdPrefix + Path.GetFileName(path),
            DisplayName = Path.GetFileNameWithoutExtension(path),
            Subtitle = $"Your own model · {bytes / 1e9:0.0} GB",
            DownloadUrl = "",
            FileName = Path.GetFileName(path),
            ApproxSizeBytes = bytes,
            ContextLength = 4096,
            SupportsFillInMiddle = false,
        };
    }

    /// <summary>Environment variable that points the model cache at another folder.</summary>
    public const string DirectoryOverrideVariable = "COTABBY_MODELS_DIR";

    /// <summary>Where GGUF files are cached. Defaults to %LOCALAPPDATA%\Cotabby\models.</summary>
    public static string DefaultLocalDirectory() =>
        Environment.GetEnvironmentVariable(DirectoryOverrideVariable) is { Length: > 0 } custom
            ? custom
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Cotabby", "models");
}
