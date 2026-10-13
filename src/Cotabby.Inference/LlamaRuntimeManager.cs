using System.Diagnostics;
using Cotabby.Core.Models;
using LLama;
using LLama.Common;
using Microsoft.Extensions.Logging;

namespace Cotabby.Inference;

/// <summary>
/// Owns the loaded <see cref="LLamaWeights"/> and the persistent <see cref="LlamaRuntimeCore"/>.
/// This type loads and unloads models. The core serializes access to the llama context.
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
    /// Loads <paramref name="model"/> from <paramref name="path"/> and unloads the model that was
    /// resident. Safe to call from any thread.
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

            if (_core is not null && _activeModel?.Id == model.Id) return;
            UnloadInternal();

            var sw = Stopwatch.StartNew();
            // One logical core stays free for the UI and UI Automation, so prompt evaluation on
            // the CPU never starves keystroke delivery.
            int threads = Math.Max(1, Environment.ProcessorCount - 1);
            var parameters = new ModelParams(path)
            {
                ContextSize = (uint)model.ContextLength,
                // Offloads every layer when the Vulkan backend is present. The CPU backend ignores it.
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
            // The context is created once per loaded model. Creating one costs seconds on Vulkan
            // because of pipeline compilation.
            var context = weights.CreateContext(parameters, _loggerFactory.CreateLogger<LLamaContext>());
            var core = new LlamaRuntimeCore(weights, context, _loggerFactory.CreateLogger<LlamaRuntimeCore>());

            // The first generation on a fresh Vulkan context compiles shaders and reserves buffers,
            // which takes two to four seconds. Paying that here keeps the first real suggestion fast.
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
