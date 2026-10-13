using System.Net;
using System.Text;
using System.Text.Json;
using Cotabby.Inference.OpenAICompatible;

namespace Cotabby.Inference.Tests;

/// <summary>One request as the fake server saw it.</summary>
internal sealed record RecordedRequest(
    HttpMethod Method,
    string Url,
    string? Authorization,
    string? Accept,
    string? ContentType,
    string Body)
{
    public JsonElement Json => JsonDocument.Parse(Body).RootElement;
}

/// <summary>Stands in for the endpoint so no network or model is involved.</summary>
internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    public List<RecordedRequest> Requests { get; } = [];

    public Func<RecordedRequest, CancellationToken, Task<HttpResponseMessage>> Responder { get; set; } =
        (_, _) => throw new InvalidOperationException("No request was expected.");

    public void Respond(Func<RecordedRequest, HttpResponseMessage> responder) =>
        Responder = (request, _) => Task.FromResult(responder(request));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var recorded = new RecordedRequest(
            request.Method,
            request.RequestUri!.AbsoluteUri,
            request.Headers.Authorization?.ToString(),
            request.Headers.Accept.ToString() is { Length: > 0 } accept ? accept : null,
            request.Content?.Headers.ContentType?.ToString(),
            request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct));
        Requests.Add(recorded);
        return await Responder(recorded, ct);
    }

    public static HttpResponseMessage Json(string body, HttpStatusCode statusCode = HttpStatusCode.OK) =>
        new(statusCode) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Sse(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/event-stream") };
}

internal sealed class InMemoryCredentialStore(string? apiKey = null) : IOpenAICompatibleCredentialStore
{
    private string? _apiKey = apiKey;

    public string? ReadApiKey() => _apiKey;

    public void SaveApiKey(string? apiKey) => _apiKey = apiKey?.Trim() is { Length: > 0 } key ? key : null;

    public void DeleteApiKey() => _apiKey = null;
}
