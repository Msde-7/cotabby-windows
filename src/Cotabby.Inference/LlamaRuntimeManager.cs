using System.Diagnostics;
using Cotabby.Core.Models;
using LLama;
using LLama.Common;
using Microsoft.Extensions.Logging;

namespace Cotabby.Inference;

/// <summary>
/// Owns the lifecycle of a single in-memory <see cref="LLamaWeights"/> plus a
/// persistent <see cref="LlamaRuntimeCore"/>. Mirrors the macOS port's
/// <c>LlamaRuntimeManager</c> + <c>LlamaRuntimeCore</c> split: this type is the
/// public, observable face; the core serializes access to the llama context.
/// </summary>
public sealed class LlamaRuntimeManager : IAsyncDisposable
{
    private readonly ILogger<LlamaRuntimeManager> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private LLamaWeights? _weights;
    private LlamaRuntimeCore? _core;
    private CotabbyModel? _activeModel;
    private bool _disposed;

    public LlamaRuntimeManager(ILogger<LlamaRuntimeManager> logger, ILoggerFactory? loggerFactory = null)
    {
        _logger = logger;
        _loggerFactory = loggerFactory ?? Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance;
    }

    /// <summary>The model currently resident in memory, or null if none.</summary>
    public CotabbyModel? ActiveModel => _activeModel;

    /// <summary>True if a model is loaded and ready to generate.</summary>
    public bool IsReady => _core is not null;

    /// <summary>The persistent decode core. Null until a model is loaded.</summary>
    public LlamaRuntimeCore? Core => _core;

    /// <summary>
    /// Load <paramref name="model"/> from <paramref name="path"/>. Unloads any
    /// previously resident model. Safe to call from any thread.
    /// </summary>
    public async Task LoadAsync(CotabbyModel model, string path, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!File.Exists(path))
            {
                throw new FileNotFoundException(
                    $"Model file not found at '{path}'. Download it before calling LoadAsync.", path);
            }

            UnloadInternal();

            var sw = Stopwatch.StartNew();
            // Reserve one logical core for the UI/UIA pump so prompt eval never
            // starves keystroke delivery when we run on CPU.
            int threads = Math.Max(1, Environment.ProcessorCount - 1);
            var parameters = new ModelParams(path)
            {
                ContextSize = (uint)model.ContextLength,
                // Offload every layer when a GPU backend (Vulkan) is present —
                // mirrors the macOS port's gpuLayerCount -1 (full Metal
                // offload). The CPU backend ignores this.
                GpuLayerCount = 999,
                Threads = threads,
                BatchThreads = threads,
                BatchSize = 512,
                UBatchSize = 512,
            };

            _logger.LogInformation(
                "Loading model {Model} from {Path} (ctx={Ctx})…",
                model.Id, path, model.ContextLength);

            var weights = await LLamaWeights.LoadFromFileAsync(parameters, ct).ConfigureAwait(false);
            // The context is created ONCE and lives for the whole model
            // residency — context creation costs seconds on Vulkan (pipeline
            // compilation), which is why the per-request StatelessExecutor
            // approach felt so slow.
            var context = weights.CreateContext(parameters, _loggerFactory.CreateLogger<LLamaContext>());
            var core = new LlamaRuntimeCore(weights, context, _loggerFactory.CreateLogger<LlamaRuntimeCore>());

            // Warm the decode path now: the first generation on a fresh Vulkan
            // context compiles shaders and reserves buffers (~2-4s). Paying
            // that at load keeps the first real suggestion inside its latency
            // budget.
            var warmupTokens = core.TokenizeText("Hello", addBos: core.Vocab.ShouldAddBOS);
            _ = await core.GenerateAsync(
                warmupTokens,
                new LlamaGenerationOptions { MaxTokens = 4, SingleLine = true },
                ct).ConfigureAwait(false);

            _weights = weights;
            _core = core;
            _activeModel = model;

            sw.Stop();
            _logger.LogInformation(
                "Model {Model} loaded in {Ms} ms (fim={Fim}).",
                model.Id, sw.ElapsedMilliseconds, core.SupportsFillInMiddle);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Unload the resident model and free its weights.</summary>
    public async Task UnloadAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { UnloadInternal(); }
        finally { _gate.Release(); }
    }

    private void UnloadInternal()
    {
        _core?.Dispose();
        _core = null;
        if (_weights is not null)
        {
            _weights.Dispose();
            _weights = null;
        }
        _activeModel = null;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await UnloadAsync().ConfigureAwait(false);
        _gate.Dispose();
    }
}
