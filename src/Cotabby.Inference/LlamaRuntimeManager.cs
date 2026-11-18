using System.Diagnostics;
using Cotabby.Core.Models;
using LLama;
using LLama.Common;
using LLama.Native;
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
            await UnloadOffCallerAsync().ConfigureAwait(false);

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

            // The previous model is already gone, so anything built here that never becomes the
            // active model is freed on failure or cancellation. Weights and a context hold
            // gigabytes of native and GPU memory that the garbage collector cannot see.
            LLamaWeights? weights = null;
            LLamaContext? context = null;
            LlamaRuntimeCore? core = null;
            try
            {
                try
                {
                    (weights, context) = await LoadWeightsAndContextAsync(parameters, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // An integrated or small GPU can run out of memory for the weights or the
                    // context. The CPU still runs the model, and no model at all would be worse.
                    _logger.LogWarning(ex, "Loading model {Model} with GPU offload failed. Retrying on the CPU.", model.Id);
                    parameters = parameters with { GpuLayerCount = 0, NoKqvOffload = true };
                    (weights, context) = await LoadWeightsAndContextAsync(parameters, ct).ConfigureAwait(false);
                }
                bool checkpoints = UsePartialCheckpoints(parameters);
                core = new LlamaRuntimeCore(
                    weights, context, _loggerFactory.CreateLogger<LlamaRuntimeCore>(), checkpoints);
                _logger.LogInformation("Prompt checkpoints for hybrid models are {State}.", checkpoints ? "on" : "off");

                // The first generation on a fresh Vulkan context compiles shaders and reserves buffers,
                // which takes two to four seconds. Paying that here keeps the first real suggestion fast.
                _ = await core.GenerateAsync(
                    "Hello", new LlamaGenerationOptions { MaxTokens = 4, SingleLine = true }, ct).ConfigureAwait(false);
            }
            catch
            {
                // The core owns the context, so it is disposed through the core once one exists.
                if (core is not null) core.Dispose();
                else context?.Dispose();
                weights?.Dispose();
                throw;
            }

            _weights = weights;
            _core = core;
            _activeModel = model;

            sw.Stop();
            _logger.LogInformation(
                "Model {Model} loaded in {Ms} ms.", model.Id, sw.ElapsedMilliseconds);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Whether hybrid models rewind to a saved checkpoint. This llama.cpp can only keep the
    /// checkpoint in host memory, and copying it out of the GPU measured about 150 ms more per
    /// request than reading the whole prompt again there, while on the CPU it saves seconds. So
    /// checkpoints run only when the model runs on the CPU. COTABBY_PROMPT_CHECKPOINTS set to 1 or
    /// 0 overrides the choice, for measuring.
    /// </summary>
    private static bool UsePartialCheckpoints(ModelParams parameters) =>
        Environment.GetEnvironmentVariable("COTABBY_PROMPT_CHECKPOINTS") switch
        {
            "1" => true,
            "0" => false,
            _ => parameters.GpuLayerCount == 0 || !NativeApi.llama_supports_gpu_offload(),
        };

    /// <summary>Loads the weights and creates their context, freeing the weights if the context fails.</summary>
    private async Task<(LLamaWeights Weights, LLamaContext Context)> LoadWeightsAndContextAsync(
        ModelParams parameters, CancellationToken ct)
    {
        var weights = await LLamaWeights.LoadFromFileAsync(parameters, ct).ConfigureAwait(false);
        try
        {
            // The context is created once per loaded model. Creating one costs seconds on Vulkan
            // because of pipeline compilation.
            return (weights, weights.CreateContext(parameters, _loggerFactory.CreateLogger<LLamaContext>()));
        }
        catch
        {
            weights.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Unloads on the thread pool. Disposing the core waits for the decode in flight to stop, and
    /// the gate above is often taken synchronously, so the caller, usually the UI thread, would
    /// otherwise do that wait itself.
    /// </summary>
    private Task UnloadOffCallerAsync() => Task.Run(UnloadInternal, CancellationToken.None);

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

    /// <summary>
    /// Frees the loaded model and its context, if any. A load in progress finishes first, then
    /// its model is freed. Safe to call from any thread, and does nothing once disposed.
    /// </summary>
    public async Task UnloadAsync()
    {
        if (_disposed) return;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_core is null && _weights is null) return;
            var model = _activeModel?.Id;
            await UnloadOffCallerAsync().ConfigureAwait(false);
            _logger.LogInformation("Model {Model} unloaded.", model);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _gate.WaitAsync().ConfigureAwait(false);
        try { await UnloadOffCallerAsync().ConfigureAwait(false); }
        finally { _gate.Release(); }
        _gate.Dispose();
    }
}
