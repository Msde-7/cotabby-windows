using System.Net;
using Cotabby.Core.Suggestions;
using Cotabby.Inference.OpenAICompatible;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cotabby.Inference.Tests;

public class OpenAICompatibleSuggestionEngineTests : IDisposable
{
    private readonly StubHttpMessageHandler _server = new();
    private readonly OpenAICompatibleApiClient _client;

    public OpenAICompatibleSuggestionEngineTests() => _client = new OpenAICompatibleApiClient(_server);

    public void Dispose() => _client.Dispose();

    private static readonly SuggestionRequest Request = new()
    {
        RequestId = "req-1",
        Prefix = "Hello",
        Suffix = "",
        HostApp = "notepad",
        SingleLine = false,
        MaxTokens = 8,
    };

    private static OpenAICompatibleEndpointSettings Settings(
        string baseUrl = OpenAICompatibleEndpointConfiguration.DefaultBaseUrl,
        string modelName = "local-model",
        OpenAICompatibleApiMode mode = OpenAICompatibleApiMode.Completions) =>
        new(baseUrl, modelName, mode);

    private OpenAICompatibleSuggestionEngine MakeEngine(
        OpenAICompatibleEndpointSettings? settings = null,
        string? apiKey = null) =>
        new(
            _client,
            new InMemoryCredentialStore(apiKey),
            settings ?? Settings(),
            NullLogger<OpenAICompatibleSuggestionEngine>.Instance);

    private static async Task<List<SuggestionChunk>> Collect(
        OpenAICompatibleSuggestionEngine engine,
        CancellationToken ct = default)
    {
        var chunks = new List<SuggestionChunk>();
        await foreach (var chunk in engine.GenerateAsync(Request, ct))
        {
            chunks.Add(chunk);
        }
        return chunks;
    }

    [Fact]
    public async Task Generation_SendsRequestKnobsAndYieldsNormalizedTextThenFinalChunk()
    {
        _server.Respond(_ => StubHttpMessageHandler.Sse("data: {\"choices\":[{\"text\":\" world\"}]}\n\ndata: [DONE]\n\n"));
        var engine = MakeEngine(apiKey: "key-123");

        var chunks = await Collect(engine);

        Assert.True(engine.IsReady);
        Assert.Equal([new SuggestionChunk(" world", false), new SuggestionChunk("", true)], chunks);
        var request = Assert.Single(_server.Requests);
        Assert.Equal("http://127.0.0.1:11434/v1/completions", request.Url);
        Assert.Equal("Bearer key-123", request.Authorization);
        var json = request.Json;
        Assert.Equal("local-model", json.GetProperty("model").GetString());
        Assert.Equal("Hello", json.GetProperty("prompt").GetString());
        Assert.Equal(8, json.GetProperty("max_tokens").GetInt32());
        Assert.Equal(0.1, json.GetProperty("temperature").GetDouble());
        Assert.Equal(0.7, json.GetProperty("top_p").GetDouble());
    }

    [Fact]
    public async Task Generation_YieldsOnlyTheFinalChunkWhenTheOutputNormalizesToEmpty()
    {
        _server.Respond(_ => StubHttpMessageHandler.Sse("data: {\"choices\":[{\"text\":\"  \"}]}\n\ndata: [DONE]\n\n"));

        Assert.Equal([new SuggestionChunk("", true)], await Collect(MakeEngine()));
    }

    [Fact]
    public async Task UpdateSettings_ChangesTheRouteAndModelOfLaterRequests()
    {
        _server.Respond(_ => StubHttpMessageHandler.Sse(
            "data: {\"choices\":[{\"delta\":{\"content\":\" there\"}}]}\n\ndata: [DONE]\n\n"));
        var engine = MakeEngine();

        engine.UpdateSettings(Settings("http://192.168.1.50:8000", "other-model", OpenAICompatibleApiMode.ChatCompletions));
        var chunks = await Collect(engine);

        Assert.Equal(" there", chunks[0].Text);
        var request = Assert.Single(_server.Requests);
        Assert.Equal("http://192.168.1.50:8000/v1/chat/completions", request.Url);
        Assert.Equal("other-model", request.Json.GetProperty("model").GetString());
        Assert.EndsWith("\n\nHello", request.Json.GetProperty("messages")[0].GetProperty("content").GetString());
    }

    [Theory]
    [InlineData("http://models.example.com/v1", "local-model", OpenAICompatibleEndpointError.InsecurePublicHttp)]
    [InlineData("not a url", "local-model", OpenAICompatibleEndpointError.InvalidBaseUrl)]
    [InlineData(OpenAICompatibleEndpointConfiguration.DefaultBaseUrl, "  ", OpenAICompatibleEndpointError.EmptyModelName)]
    public async Task ConfigurationAndModelErrors_MakeTheEngineUnavailable(
        string baseUrl, string modelName, OpenAICompatibleEndpointError expected)
    {
        var engine = MakeEngine(Settings(baseUrl, modelName));

        Assert.False(engine.IsReady);
        Assert.Equal(OpenAICompatibleEndpointException.Describe(expected), engine.UnavailableReason);
        var ex = await Assert.ThrowsAsync<OpenAICompatibleEndpointException>(() => Collect(engine));
        Assert.Equal(expected, ex.Error);
        Assert.Empty(_server.Requests);

        engine.UpdateSettings(Settings());
        Assert.True(engine.IsReady);
        Assert.Null(engine.UnavailableReason);
    }

    [Fact]
    public async Task TransportAndStreamErrors_AreThrownWithTheirUserFacingMessage()
    {
        _server.Respond(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var status = await Assert.ThrowsAsync<OpenAICompatibleClientException>(() => Collect(MakeEngine()));
        Assert.Equal("The endpoint returned HTTP 500.", status.Message);

        _server.Respond(_ => StubHttpMessageHandler.Sse("data: {\"error\":{\"message\":\"model missing\"}}\n\n"));
        var stream = await Assert.ThrowsAsync<OpenAICompatibleClientException>(() => Collect(MakeEngine()));
        Assert.Equal("The endpoint reported an error: model missing", stream.Message);
    }

    [Fact]
    public async Task Cancellation_EndsTheStreamWithoutChunksOrErrors()
    {
        var started = new TaskCompletionSource();
        _server.Responder = async (_, ct) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("unreachable");
        };
        using var cancellation = new CancellationTokenSource();

        var generation = Collect(MakeEngine(), cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        Assert.Empty(await generation);
    }

    [Fact]
    public async Task Prewarm_PreloadsOnlyTheDefaultOllamaEndpoint()
    {
        _server.Respond(_ => StubHttpMessageHandler.Json("""{"done":true}"""));

        await MakeEngine(Settings("https://models.example.com/v1")).PrewarmAsync();
        Assert.Empty(_server.Requests);

        await MakeEngine().PrewarmAsync();
        Assert.Equal(["http://127.0.0.1:11434/api/generate"], _server.Requests.Select(request => request.Url));
    }

    [Fact]
    public async Task Prewarm_SwallowsPreloadFailures()
    {
        _server.Respond(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        await MakeEngine().PrewarmAsync();

        Assert.Single(_server.Requests);
    }
}

public class OllamaPreloadWorkControllerTests
{
    [Fact]
    public async Task SameModelConcurrentWarmupsShareOneOperationAndAllowLaterRetry()
    {
        var controller = new OllamaPreloadWorkController();
        var started = new TaskCompletionSource();
        var finishFirst = new TaskCompletionSource();
        var invocations = 0;

        var first = controller.RunAsync("model-a", async () =>
        {
            Interlocked.Increment(ref invocations);
            started.SetResult();
            await finishFirst.Task;
        });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = controller.RunAsync("model-a", () =>
        {
            Interlocked.Increment(ref invocations);
            return Task.CompletedTask;
        });
        Assert.False(second.IsCompleted);
        Assert.Equal(1, invocations);

        finishFirst.SetResult();
        await Task.WhenAll(first, second);

        await controller.RunAsync("model-a", () =>
        {
            Interlocked.Increment(ref invocations);
            return Task.CompletedTask;
        });
        Assert.Equal(2, invocations);
    }

    [Fact]
    public async Task DifferentModelsMayPreloadIndependently()
    {
        var controller = new OllamaPreloadWorkController();
        var release = new TaskCompletionSource();
        var startedA = new TaskCompletionSource();
        var startedB = new TaskCompletionSource();

        var first = controller.RunAsync("model-a", async () =>
        {
            startedA.SetResult();
            await release.Task;
        });
        var second = controller.RunAsync("model-b", async () =>
        {
            startedB.SetResult();
            await release.Task;
        });

        await Task.WhenAll(startedA.Task, startedB.Task).WaitAsync(TimeSpan.FromSeconds(5));
        release.SetResult();
        await Task.WhenAll(first, second);
    }
}
