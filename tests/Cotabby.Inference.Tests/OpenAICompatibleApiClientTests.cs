using System.Net;
using Cotabby.Inference.OpenAICompatible;
using Mode = Cotabby.Inference.OpenAICompatible.OpenAICompatibleApiMode;
using SseEvent = Cotabby.Inference.OpenAICompatible.OpenAICompatibleSseEvent;

namespace Cotabby.Inference.Tests;

public class OpenAICompatibleApiClientTests : IDisposable
{
    private static readonly OpenAICompatibleGenerationOptions Options = new(8, 0.1, 0.7);

    private readonly StubHttpMessageHandler _server = new();
    private readonly OpenAICompatibleApiClient _client;

    public OpenAICompatibleApiClientTests() => _client = new OpenAICompatibleApiClient(_server);

    public void Dispose() => _client.Dispose();

    private static OpenAICompatibleEndpointConfiguration Configuration(
        string baseUrl = OpenAICompatibleEndpointConfiguration.DefaultBaseUrl,
        Mode mode = Mode.ChatCompletions,
        string modelName = "gemma4:12b-mlx") =>
        new(baseUrl, modelName, mode);

    public static TheoryData<string, Mode, SseEvent> SseLines => new()
    {
        { """data: {"choices":[{"text":" hel"}]}""", Mode.Completions, SseEvent.Text(" hel") },
        { """{"choices":[{"text":"bare"}]}""", Mode.Completions, SseEvent.Text("bare") },
        { """data: {"choices":[{"delta":{"content":"lo"}}]}""", Mode.ChatCompletions, SseEvent.Text("lo") },
        { """data: {"choices":[{"message":{"content":"whole"}}]}""", Mode.ChatCompletions, SseEvent.Text("whole") },
        {
            """data: {"choices":[{"delta":{"content":"d"},"message":{"content":"m"}}]}""",
            Mode.ChatCompletions,
            SseEvent.Text("d")
        },
        // Field and mode mismatches and empty payloads carry no visible text.
        { """data: {"choices":[{"delta":{"content":"chat"}}]}""", Mode.Completions, SseEvent.Ignore },
        { """data: {"choices":[{"text":"legacy"}]}""", Mode.ChatCompletions, SseEvent.Ignore },
        { """data: {"choices":[{"delta":{"role":"assistant"}}]}""", Mode.ChatCompletions, SseEvent.Ignore },
        { """data: {"choices":[{"text":""}]}""", Mode.Completions, SseEvent.Ignore },
        { """data: {"choices":[]}""", Mode.Completions, SseEvent.Ignore },
        { "data: {}", Mode.Completions, SseEvent.Ignore },
        // Blank lines, comments and metadata fields are never payloads.
        { "", Mode.Completions, SseEvent.Ignore },
        { "   ", Mode.Completions, SseEvent.Ignore },
        { ": keep-alive", Mode.Completions, SseEvent.Ignore },
        { "event: completion", Mode.Completions, SseEvent.Ignore },
        { "id: 42", Mode.Completions, SseEvent.Ignore },
        { "retry: 1000", Mode.Completions, SseEvent.Ignore },
        // The terminator is recognized with or without the space, the prefix or a CRLF remnant.
        { "data: [DONE]", Mode.Completions, SseEvent.Done },
        { "data:[DONE]", Mode.ChatCompletions, SseEvent.Done },
        { "[DONE]", Mode.Completions, SseEvent.Done },
        { "data: [DONE]\r", Mode.Completions, SseEvent.Done },
        // A non-empty error wins over any choices and an empty message is not an error.
        { """data: {"error":{"message":"model missing"}}""", Mode.Completions, SseEvent.Error("model missing") },
        {
            """data: {"error":{"message":"boom"},"choices":[{"text":"x"}]}""",
            Mode.Completions,
            SseEvent.Error("boom")
        },
        { """data: {"error":{"message":""}}""", Mode.Completions, SseEvent.Ignore },
    };

    [Theory]
    [MemberData(nameof(SseLines))]
    public void SseDecoder_MapsEachLineShapeToOneEvent(string line, Mode mode, SseEvent expected) =>
        Assert.Equal(expected, OpenAICompatibleSseDecoder.Decode(line, mode));

    [Theory]
    [InlineData("data: {not json")]
    [InlineData("data: plain text")]
    [InlineData("""data: {"choices":"nope"}""")]
    [InlineData("""data: {"error":{}}""")]
    public void SseDecoder_ThrowsMalformedResponseForNonChunkPayloads(string line)
    {
        var ex = Assert.Throws<OpenAICompatibleClientException>(
            () => OpenAICompatibleSseDecoder.Decode(line, Mode.Completions));
        Assert.Equal(OpenAICompatibleClientError.MalformedResponse, ex.Error);
    }

    [Fact]
    public void Timeouts_MatchTheEndpointContract()
    {
        Assert.Equal(TimeSpan.FromSeconds(10), OpenAICompatibleApiClient.ModelListTimeout);
        Assert.Equal(TimeSpan.FromSeconds(120), OpenAICompatibleApiClient.GenerationTimeout);
    }

    [Fact]
    public async Task FetchModels_UsesModelsRouteAndBearerAuthorization()
    {
        _server.Respond(_ => StubHttpMessageHandler.Json(
            """{"object":"list","data":[{"id":"zeta"},{"id":"alpha","owned_by":"library"}]}"""));

        var models = await _client.FetchModelsAsync(Configuration(), "secret");

        Assert.Equal(["alpha", "zeta"], models.Select(model => model.Id));
        var request = Assert.Single(_server.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("http://127.0.0.1:11434/v1/models", request.Url);
        Assert.Equal("Bearer secret", request.Authorization);
    }

    [Fact]
    public async Task FetchModels_MapsNonSuccessStatusAndMalformedCatalog()
    {
        _server.Respond(_ => StubHttpMessageHandler.Json("{}", HttpStatusCode.TooManyRequests));
        var status = await Assert.ThrowsAsync<OpenAICompatibleClientException>(
            () => _client.FetchModelsAsync(Configuration(), null));
        Assert.Equal(OpenAICompatibleClientError.Server, status.Error);
        Assert.Equal(429, status.StatusCode);

        _server.Respond(_ => StubHttpMessageHandler.Json("{}"));
        var malformed = await Assert.ThrowsAsync<OpenAICompatibleClientException>(
            () => _client.FetchModelsAsync(Configuration(), null));
        Assert.Equal(OpenAICompatibleClientError.MalformedResponse, malformed.Error);
    }

    [Fact]
    public async Task CompletionGeneration_PostsStandardPayloadAndAccumulatesSse()
    {
        _server.Respond(_ => StubHttpMessageHandler.Sse(
            "data: {\"choices\":[{\"text\":\" hel\"}]}\n\n" +
            "data: {\"choices\":[{\"text\":\"lo\"}]}\n\n" +
            "data: [DONE]\n\n"));

        var output = await _client.GenerateAsync(
            Configuration(mode: Mode.Completions), null, "Complete this", new(12, 0.2, 0.8));

        Assert.Equal(" hello", output);
        var request = Assert.Single(_server.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("http://127.0.0.1:11434/v1/completions", request.Url);
        Assert.Equal("application/json", request.ContentType);
        Assert.Equal("text/event-stream", request.Accept);
        Assert.Null(request.Authorization);
        var json = request.Json;
        Assert.Equal("gemma4:12b-mlx", json.GetProperty("model").GetString());
        Assert.Equal("Complete this", json.GetProperty("prompt").GetString());
        Assert.True(json.GetProperty("stream").GetBoolean());
        Assert.Equal(12, json.GetProperty("max_tokens").GetInt32());
        Assert.Equal(0.2, json.GetProperty("temperature").GetDouble());
        Assert.Equal(0.8, json.GetProperty("top_p").GetDouble());
        Assert.Equal(6, json.EnumerateObject().Count());
    }

    [Fact]
    public async Task ChatGeneration_PostsSingleUserMessageAndReadsDeltaContent()
    {
        _server.Respond(_ => StubHttpMessageHandler.Sse(
            "data: {\"choices\":[{\"delta\":{\"content\":\" next\"}}]}\n\n" +
            "data: [DONE]\n\n"));

        var output = await _client.GenerateAsync(Configuration(), null, "Continue me", Options);

        Assert.Equal(" next", output);
        var request = Assert.Single(_server.Requests);
        Assert.Equal("http://127.0.0.1:11434/v1/chat/completions", request.Url);
        var json = request.Json;
        var message = Assert.Single(json.GetProperty("messages").EnumerateArray());
        Assert.Equal("user", message.GetProperty("role").GetString());
        Assert.Equal(
            "Continue the text at the end of the context. Reply with only new continuation " +
            "text; do not repeat or quote existing text.\n\nContinue me",
            message.GetProperty("content").GetString());
        Assert.True(json.GetProperty("stream").GetBoolean());
        Assert.Equal(8, json.GetProperty("max_tokens").GetInt32());
        Assert.Equal(0.1, json.GetProperty("temperature").GetDouble());
        Assert.Equal(0.7, json.GetProperty("top_p").GetDouble());
        Assert.Equal("none", json.GetProperty("reasoning_effort").GetString());
        Assert.Equal(7, json.EnumerateObject().Count());
    }

    [Fact]
    public async Task DefaultOllamaPreload_UsesNativeRouteAndKeepsModelResident()
    {
        _server.Respond(_ => StubHttpMessageHandler.Json("""{"done":true}"""));

        Assert.True(await _client.PreloadDefaultOllamaModelAsync(Configuration(), null));

        var request = Assert.Single(_server.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("http://127.0.0.1:11434/api/generate", request.Url);
        Assert.Equal("application/json", request.ContentType);
        var json = request.Json;
        Assert.Equal("gemma4:12b-mlx", json.GetProperty("model").GetString());
        Assert.Equal("", json.GetProperty("prompt").GetString());
        Assert.False(json.GetProperty("stream").GetBoolean());
        Assert.Equal(-1, json.GetProperty("keep_alive").GetInt32());
    }

    [Fact]
    public async Task NonDefaultEndpoint_DoesNotReceiveOllamaPreloadRequest()
    {
        Assert.False(await _client.PreloadDefaultOllamaModelAsync(Configuration("https://models.example.com/v1"), null));
        Assert.Empty(_server.Requests);
    }

    [Fact]
    public async Task Generation_MapsNonSuccessStatus()
    {
        _server.Respond(_ => StubHttpMessageHandler.Json(
            """{"error":{"message":"unauthorized"}}""", HttpStatusCode.Unauthorized));

        var ex = await Assert.ThrowsAsync<OpenAICompatibleClientException>(
            () => _client.GenerateAsync(Configuration(), null, "text", Options));

        Assert.Equal(OpenAICompatibleClientError.Server, ex.Error);
        Assert.Equal(401, ex.StatusCode);
        Assert.Equal("The endpoint returned HTTP 401.", ex.Message);
    }

    [Fact]
    public async Task GenerationCancellation_StopsTheUnderlyingRequest()
    {
        var started = new TaskCompletionSource();
        var stopped = new TaskCompletionSource();
        _server.Responder = async (_, ct) =>
        {
            started.SetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            finally
            {
                stopped.SetResult();
            }
            throw new InvalidOperationException("unreachable");
        };
        using var cancellation = new CancellationTokenSource();

        var generation = _client.GenerateAsync(Configuration(), null, "text", Options, cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => generation);
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Generation_TrimsApiKeyAndOmitsAuthorizationForBlankKeys()
    {
        _server.Respond(_ => StubHttpMessageHandler.Sse("data: [DONE]\n\n"));

        foreach (var apiKey in new[] { "  secret\n", "   ", null })
        {
            await _client.GenerateAsync(Configuration(), apiKey, "text", Options);
        }

        Assert.Equal(["Bearer secret", null, null], _server.Requests.Select(request => request.Authorization));
    }

    [Fact]
    public async Task Generation_BlankModelNameFailsBeforeAnyRequest()
    {
        var blankModel = Configuration(modelName: "   ");

        var generate = await Assert.ThrowsAsync<OpenAICompatibleEndpointException>(
            () => _client.GenerateAsync(blankModel, null, "text", Options));
        var preload = await Assert.ThrowsAsync<OpenAICompatibleEndpointException>(
            () => _client.PreloadDefaultOllamaModelAsync(blankModel, null));

        Assert.Equal(OpenAICompatibleEndpointError.EmptyModelName, generate.Error);
        Assert.Equal(OpenAICompatibleEndpointError.EmptyModelName, preload.Error);
        Assert.Empty(_server.Requests);
    }

    [Fact]
    public async Task Generation_StreamErrorEventThrows()
    {
        _server.Respond(_ => StubHttpMessageHandler.Sse(
            "data: {\"choices\":[{\"text\":\" par\"}]}\n\n" +
            "data: {\"error\":{\"message\":\"model unloaded\"}}\n\n" +
            "data: {\"choices\":[{\"text\":\"never\"}]}\n\n"));

        var ex = await Assert.ThrowsAsync<OpenAICompatibleClientException>(
            () => _client.GenerateAsync(Configuration(mode: Mode.Completions), null, "text", Options));

        Assert.Equal(OpenAICompatibleClientError.StreamError, ex.Error);
        Assert.Equal("The endpoint reported an error: model unloaded", ex.Message);
    }

    // A server that closes the stream without [DONE] still produced a usable answer, but a body
    // with no events at all is not an OpenAI-compatible stream.
    [Fact]
    public async Task Generation_StreamEndWithoutDoneReturnsTextButEmptyStreamIsMalformed()
    {
        var configuration = Configuration(mode: Mode.Completions);
        _server.Respond(_ => StubHttpMessageHandler.Sse("data: {\"choices\":[{\"text\":\" tail\"}]}\n\n"));
        Assert.Equal(" tail", await _client.GenerateAsync(configuration, null, "text", Options));

        _server.Respond(_ => StubHttpMessageHandler.Sse(": keep-alive\n\n\n"));
        var ex = await Assert.ThrowsAsync<OpenAICompatibleClientException>(
            () => _client.GenerateAsync(configuration, null, "text", Options));
        Assert.Equal(OpenAICompatibleClientError.MalformedResponse, ex.Error);
    }

    [Fact]
    public async Task ConnectionModel_PublishesReadyFailedAndIdleStates()
    {
        var connection = new OpenAICompatibleConnectionModel(_client);
        var changes = new List<string?>();
        connection.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
        _server.Respond(_ => StubHttpMessageHandler.Json("""{"data":[{"id":"b"},{"id":"a"}]}"""));

        await connection.RefreshAsync(Configuration(), null);
        Assert.Equal(["a", "b"], connection.Models.Select(model => model.Id));
        Assert.Equal(new OpenAICompatibleConnectionState.Ready(2), connection.State);
        Assert.Equal("Connected · 2 models", connection.State.Summary);
        Assert.Contains(nameof(connection.State), changes);
        Assert.Contains(nameof(connection.Models), changes);

        _server.Respond(_ => StubHttpMessageHandler.Json("{}", HttpStatusCode.ServiceUnavailable));
        await connection.RefreshAsync(Configuration(), null);
        Assert.Empty(connection.Models);
        Assert.Equal(new OpenAICompatibleConnectionState.Failed("The endpoint returned HTTP 503."), connection.State);

        connection.Invalidate();
        Assert.Equal(new OpenAICompatibleConnectionState.Idle(), connection.State);

        connection.SetFailure("Credential store unavailable");
        Assert.Equal("Credential store unavailable", connection.State.FailureDetail);
    }

    [Fact]
    public async Task ConnectionModel_IgnoresASlowResponseForAnOlderRefresh()
    {
        var connection = new OpenAICompatibleConnectionModel(_client);
        var release = new TaskCompletionSource();
        _server.Responder = async (_, _) =>
        {
            await release.Task;
            return StubHttpMessageHandler.Json("""{"data":[{"id":"stale"}]}""");
        };

        var refresh = connection.RefreshAsync(Configuration(), null);
        connection.Invalidate();
        release.SetResult();
        await refresh;

        Assert.Empty(connection.Models);
        Assert.Equal(new OpenAICompatibleConnectionState.Idle(), connection.State);
    }
}
