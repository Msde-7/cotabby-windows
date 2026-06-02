using System.Diagnostics;
using Cotabby.Core.Models;
using LLama;
using LLama.Common;
using Microsoft.Extensions.Logging;

namespace Cotabby.Inference;

/// <summary>
/// Owns the lifecycle of a single in-memory LLamaSharp <see cref="LLamaWeights"/> +
/// <see cref="StatelessExecutor"/> pair. Mirrors the macOS port's
/// <c>LlamaRuntimeManager</c> + <c>LlamaRuntimeCore</c> split: this type is the
/// public, observable face; the loaded resources are guarded by a single
/// semaphore so concurrent calls into the executor are serialized.
/// </summary>
public sealed class LlamaRuntimeManager : IAsyncDisposable
{
    private readonly ILogger<LlamaRuntimeManager> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private LLamaWeights? _weights;
    private StatelessExecutor? _executor;
    private ModelParams? _params;
    private CotabbyModel? _activeModel;
    private bool _disposed;

    public LlamaRuntimeManager(ILogger<LlamaRuntimeManager> logger) => _logger = logger;

    /// <summary>The model currently resident in memory, or null if none.</summary>
    public CotabbyModel? ActiveModel => _activeModel;

    /// <summary>True if a model is loaded and ready to generate.</summary>
    public bool IsReady => _executor is not null && _weights is not null;

    /// <summary>Resolved file path for the model. Used by the suggestion engine.</summary>
    internal StatelessExecutor? Executor => _executor;
    internal ModelParams? Params => _params;

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
            var parameters = new ModelParams(path)
            {
                ContextSize = (uint)model.ContextLength,
                GpuLayerCount = 0,
            };

            _logger.LogInformation(
                "Loading model {Model} from {Path} (ctx={Ctx})…",
                model.Id, path, model.ContextLength);

            var weights = await LLamaWeights.LoadFromFileAsync(parameters, ct).ConfigureAwait(false);
            var executor = new StatelessExecutor(weights, parameters);

            _weights = weights;
            _executor = executor;
            _params = parameters;
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

    /// <summary>Unload the resident model and free its weights.</summary>
    public async Task UnloadAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { UnloadInternal(); }
        finally { _gate.Release(); }
    }

    private void UnloadInternal()
    {
        _executor = null;
        if (_weights is not null)
        {
            _weights.Dispose();
            _weights = null;
        }
        _params = null;
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
