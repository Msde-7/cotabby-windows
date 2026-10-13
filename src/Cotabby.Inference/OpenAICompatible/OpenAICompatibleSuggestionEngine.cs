using System.Diagnostics;
using System.Runtime.CompilerServices;
using Cotabby.Core.Suggestions;
using Cotabby.Core.Support;
using Microsoft.Extensions.Logging;

namespace Cotabby.Inference.OpenAICompatible;

/// <summary>
/// Adapts an OpenAI-compatible endpoint to <see cref="ISuggestionEngine"/>. The HTTP client owns
/// wire parsing. This type owns the settings snapshot, the prompt, sampling and normalization.
/// </summary>
/// <remarks>
/// Like the local engine it surfaces one normalized chunk, because prompt echo and trailing
/// duplication are only detectable on the full string. Configuration and transport failures are
/// thrown with their user-facing message. Cancellation ends the stream silently.
/// </remarks>
public sealed class OpenAICompatibleSuggestionEngine : ISuggestionEngine
{
    // Low temperature keeps inline completions stable and less likely to drift.
    public const double Temperature = 0.1;
    public const double TopP = 0.7;

    private readonly OpenAICompatibleApiClient _client;
    private readonly IOpenAICompatibleCredentialStore _credentials;
    private readonly ILogger<OpenAICompatibleSuggestionEngine> _logger;
    private readonly OllamaPreloadWorkController _preloads = new();
    private volatile Endpoint _endpoint;

    public OpenAICompatibleSuggestionEngine(
        OpenAICompatibleApiClient client,
        IOpenAICompatibleCredentialStore credentials,
        OpenAICompatibleEndpointSettings settings,
        ILogger<OpenAICompatibleSuggestionEngine> logger)
    {
        _client = client;
        _credentials = credentials;
        _logger = logger;
        _endpoint = Endpoint.Resolve(settings);
    }

    /// <summary>Replaces the settings snapshot. Requests already in flight keep the old one.</summary>
    public void UpdateSettings(OpenAICompatibleEndpointSettings settings) => _endpoint = Endpoint.Resolve(settings);

    public bool IsReady => _endpoint.Configuration is not null;

    public bool SpacingIsExact => false;

    /// <summary>Why the current settings cannot generate, or null when they can.</summary>
    public string? UnavailableReason =>
        _endpoint.Error is { } error ? OpenAICompatibleEndpointException.Describe(error) : null;

    public async IAsyncEnumerable<SuggestionChunk> GenerateAsync(
        SuggestionRequest request,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var endpoint = _endpoint;
        var configuration = endpoint.Configuration
            ?? throw new OpenAICompatibleEndpointException(endpoint.Error!.Value);

        var prompt = CompletionPipeline.Prompt(request, tokenBudget: null);
        if (prompt.Length == 0)
        {
            yield return new SuggestionChunk(string.Empty, IsFinal: true);
            yield break;
        }

        var started = Stopwatch.GetTimestamp();
        string raw;
        try
        {
            raw = await _client.GenerateAsync(
                configuration,
                _credentials.ReadApiKey(),
                prompt,
                new OpenAICompatibleGenerationOptions(request.MaxTokens, Temperature, TopP),
                ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            yield break;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Req {ReqId}: endpoint generation failed.", request.RequestId);
            throw;
        }

        if (ct.IsCancellationRequested) yield break;

        var normalized = CompletionPipeline.Normalize(raw, prompt, request);
        _logger.LogInformation(
            "Req {ReqId}: endpoint model {Model} answered in {Ms} ms ({Reason}).",
            request.RequestId, configuration.ModelName,
            (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds, normalized.Suppression?.ToString() ?? "shown");
        _logger.LogDebug("Req {ReqId}: raw=\"{Raw}\".", request.RequestId, LogText.Escape(raw));

        if (normalized.Suppression is null)
        {
            yield return new SuggestionChunk(normalized.Text, IsFinal: false);
        }
        yield return new SuggestionChunk(string.Empty, IsFinal: true);
    }

    /// <summary>
    /// Asks the default local Ollama server to load the selected model. Does nothing for any
    /// other endpoint and never throws. The load is independent of prediction cancellation, so
    /// typing again cannot abort a cold start that later requests need.
    /// </summary>
    public Task PrewarmAsync()
    {
        if (_endpoint.Configuration is not { DefaultOllamaGenerateUrl: not null } configuration)
        {
            return Task.CompletedTask;
        }

        return _preloads.RunAsync(configuration.ModelName, async () =>
        {
            try
            {
                await _client
                    .PreloadDefaultOllamaModelAsync(configuration, _credentials.ReadApiKey())
                    .ConfigureAwait(false);
                _logger.LogInformation("Preloaded default Ollama model {Model}.", configuration.ModelName);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Default Ollama model preload failed.");
            }
        });
    }

    /// <summary>The resolved settings snapshot. Exactly one of the two members is set.</summary>
    private sealed record Endpoint(
        OpenAICompatibleEndpointConfiguration? Configuration,
        OpenAICompatibleEndpointError? Error)
    {
        public static Endpoint Resolve(OpenAICompatibleEndpointSettings settings)
        {
            try
            {
                var configuration = new OpenAICompatibleEndpointConfiguration(settings);
                return configuration.ModelName.Length == 0
                    ? new Endpoint(null, OpenAICompatibleEndpointError.EmptyModelName)
                    : new Endpoint(configuration, null);
            }
            catch (OpenAICompatibleEndpointException ex)
            {
                return new Endpoint(null, ex.Error);
            }
        }
    }
}

/// <summary>
/// Coalesces Ollama warmups per model. The first caller starts the load and later callers await
/// that same flight. A finished flight is removed so a later call can retry after Ollama restarts.
/// </summary>
public sealed class OllamaPreloadWorkController
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Task> _flightsByModel = [];

    public Task RunAsync(string modelName, Func<Task> operation)
    {
        lock (_gate)
        {
            if (_flightsByModel.TryGetValue(modelName, out var existing)) return existing;

            return _flightsByModel[modelName] = FlyAsync(modelName, operation);
        }
    }

    private async Task FlyAsync(string modelName, Func<Task> operation)
    {
        // Hop to the thread pool first so the flight is registered before it can finish.
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        try
        {
            await operation().ConfigureAwait(false);
        }
        finally
        {
            lock (_gate) { _flightsByModel.Remove(modelName); }
        }
    }
}
