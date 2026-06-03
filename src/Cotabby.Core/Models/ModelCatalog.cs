namespace Cotabby.Core.Models;

/// <summary>
/// A model the user can download and run. Mirrors the macOS port's
/// hardcoded GGUF list — we keep the same Hugging Face URLs so the two ports
/// can share the same on-disk caches if both are installed.
/// </summary>
public sealed record CotabbyModel
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }

    /// <summary>HF download URL for the .gguf file.</summary>
    public required string DownloadUrl { get; init; }

    /// <summary>Approximate disk size in bytes — informational only.</summary>
    public required long ApproxSizeBytes { get; init; }

    /// <summary>Context window the model was trained for (token count).</summary>
    public required int ContextLength { get; init; }

    /// <summary>True if the model expects a fill-in-middle prompt template.</summary>
    public required bool SupportsFillInMiddle { get; init; }

    /// <summary>The local filename to use under the models directory.</summary>
    public string FileName => Path.GetFileName(new Uri(DownloadUrl).AbsolutePath);
}

/// <summary>
/// Built-in model list. Treated as code rather than config because the
/// number of models is small and the prompting strategy for each is
/// bound to its tokenizer.
/// </summary>
public static class ModelCatalog
{
    public static IReadOnlyList<CotabbyModel> All { get; } =
    [
        // Base variants are preferred for FIM completion — the Instruct
        // variants are chat-fine-tuned and tend to ignore <|fim_*|> tokens or
        // degenerate (repeat the prefix word, output single-token loops). The
        // 0.5B-Instruct was the original default and is kept for back-compat
        // so existing caches don't get orphaned, but the picker now shows base
        // variants first.
        // bartowski's Instruct GGUFs are the consistently open-mirror set
        // available without HF auth right now. The base variants are gated
        // (401), and Qwen's official repos require login since late 2025.
        // Instruct on a larger model still beats Instruct on a smaller one
        // for FIM completion — the tokenizer is the same and the larger
        // model just understands the <|fim_*|> tokens better.
        // ApproxSizeBytes are the *actual* Q4_K_M GGUF file sizes from
        // bartowski's repos (HEAD'd before commit). IsCached() compares
        // disk size >= ApproxSizeBytes * 0.95; if these are wrong, a
        // downloaded model is silently rejected and the app sits without a
        // runtime. Updated to match: 1.5B = 986MB, 0.5B = 396MB, etc.
        new CotabbyModel
        {
            Id = "qwen2.5-coder-1.5b-base-q4",
            DisplayName = "Qwen2.5-Coder 1.5B Instruct (Q4_K_M) — recommended",
            DownloadUrl = "https://huggingface.co/bartowski/Qwen2.5-Coder-1.5B-Instruct-GGUF/resolve/main/Qwen2.5-Coder-1.5B-Instruct-Q4_K_M.gguf",
            ApproxSizeBytes = 986L * 1024 * 1024,
            ContextLength = 4096,
            SupportsFillInMiddle = true,
        },
        new CotabbyModel
        {
            Id = "qwen2.5-coder-0.5b-base-q4",
            DisplayName = "Qwen2.5-Coder 0.5B Instruct (bartowski) — fastest",
            DownloadUrl = "https://huggingface.co/bartowski/Qwen2.5-Coder-0.5B-Instruct-GGUF/resolve/main/Qwen2.5-Coder-0.5B-Instruct-Q4_K_M.gguf",
            ApproxSizeBytes = 396L * 1024 * 1024,
            ContextLength = 4096,
            SupportsFillInMiddle = true,
        },
        new CotabbyModel
        {
            Id = "qwen2.5-coder-3b-base-q4",
            DisplayName = "Qwen2.5-Coder 3B Instruct (Q4_K_M) — higher quality",
            DownloadUrl = "https://huggingface.co/bartowski/Qwen2.5-Coder-3B-Instruct-GGUF/resolve/main/Qwen2.5-Coder-3B-Instruct-Q4_K_M.gguf",
            ApproxSizeBytes = 1929L * 1024 * 1024,
            ContextLength = 4096,
            SupportsFillInMiddle = true,
        },
        new CotabbyModel
        {
            Id = "qwen2.5-coder-7b-base-q4",
            DisplayName = "Qwen2.5-Coder 7B Instruct (Q4_K_M) — best quality",
            DownloadUrl = "https://huggingface.co/bartowski/Qwen2.5-Coder-7B-Instruct-GGUF/resolve/main/Qwen2.5-Coder-7B-Instruct-Q4_K_M.gguf",
            ApproxSizeBytes = 4683L * 1024 * 1024,
            ContextLength = 4096,
            SupportsFillInMiddle = true,
        },
        // Legacy entry — kept so the existing %LOCALAPPDATA% cache still
        // resolves and users don't get pushed into a fresh 400MB download
        // on update.
        new CotabbyModel
        {
            Id = "qwen2.5-coder-0.5b-q4",
            DisplayName = "Qwen2.5-Coder 0.5B Instruct (legacy — degenerates on small prompts)",
            DownloadUrl = "https://huggingface.co/Qwen/Qwen2.5-Coder-0.5B-Instruct-GGUF/resolve/main/qwen2.5-coder-0.5b-instruct-q4_k_m.gguf",
            ApproxSizeBytes = 400L * 1024 * 1024,
            ContextLength = 4096,
            SupportsFillInMiddle = true,
        },
    ];

    public static CotabbyModel? FindById(string id) =>
        All.FirstOrDefault(m => m.Id == id);

    /// <summary>Default OS path for cached GGUF files: %LOCALAPPDATA%\Cotabby\models.</summary>
    public static string DefaultLocalDirectory()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(local, "Cotabby", "models");
    }
}
