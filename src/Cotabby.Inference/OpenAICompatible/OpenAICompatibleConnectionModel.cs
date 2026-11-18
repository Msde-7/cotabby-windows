using System.ComponentModel;

namespace Cotabby.Inference.OpenAICompatible;

/// <summary>
/// Observable model catalog for the settings UI. Use it from the UI thread only. A refresh
/// generation prevents a slow response for an old URL from replacing a newer catalog.
/// </summary>
public sealed class OpenAICompatibleConnectionModel(OpenAICompatibleApiClient client) : INotifyPropertyChanged
{
    private ulong _refreshGeneration;

    public event PropertyChangedEventHandler? PropertyChanged;

    public IReadOnlyList<OpenAICompatibleModelOption> Models { get; private set; } = [];

    public OpenAICompatibleConnectionState State { get; private set; } = new OpenAICompatibleConnectionState.Idle();

    public void Invalidate()
    {
        _refreshGeneration++;
        Publish([], new OpenAICompatibleConnectionState.Idle());
    }

    public void SetFailure(string message)
    {
        _refreshGeneration++;
        Publish([], new OpenAICompatibleConnectionState.Failed(message));
    }

    public async Task RefreshAsync(
        OpenAICompatibleEndpointConfiguration configuration,
        string? apiKey,
        CancellationToken ct = default)
    {
        var generation = ++_refreshGeneration;
        // A refresh this one supersedes never publishes, so its Connecting is not worth restoring.
        var previous = State is OpenAICompatibleConnectionState.Connecting
            ? new OpenAICompatibleConnectionState.Idle()
            : State;
        Publish(Models, new OpenAICompatibleConnectionState.Connecting());
        try
        {
            // No ConfigureAwait(false) here so the result is published on the calling UI thread.
            var fetched = await client.FetchModelsAsync(configuration, apiKey, ct);
            if (_refreshGeneration != generation) return;
            Publish(fetched, new OpenAICompatibleConnectionState.Ready(fetched.Count));
        }
        catch (OperationCanceledException)
        {
            // Nothing newer replaced the state, so it goes back to what it was rather than
            // staying at Connecting.
            if (_refreshGeneration == generation) Publish(Models, previous);
        }
        catch (Exception ex)
        {
            if (_refreshGeneration != generation) return;
            Publish([], new OpenAICompatibleConnectionState.Failed(ex.Message));
        }
    }

    private void Publish(IReadOnlyList<OpenAICompatibleModelOption> models, OpenAICompatibleConnectionState state)
    {
        Models = models;
        State = state;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Models)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(State)));
    }
}
