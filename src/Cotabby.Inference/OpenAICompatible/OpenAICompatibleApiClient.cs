using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Cotabby.Inference.OpenAICompatible;

/// <summary>Sampling values shared by the completions and chat-completions request shapes.</summary>
public sealed record OpenAICompatibleGenerationOptions(int MaxPredictionTokens, double Temperature, double TopP);

/// <summary>A failed endpoint exchange. The message is user-facing.</summary>
public sealed class OpenAICompatibleClientException : Exception
{
    private OpenAICompatibleClientException(string message) : base(message)
    {
    }

    /// <param name="detail">The server's own explanation, already shortened and free of secrets.</param>
    public static OpenAICompatibleClientException Server(int statusCode, string? detail = null) =>
        new(detail is { Length: > 0 }
            ? $"The endpoint returned HTTP {statusCode}: {detail}"
            : $"The endpoint returned HTTP {statusCode}.");

    public static OpenAICompatibleClientException MalformedResponse() =>
        new("The endpoint returned an invalid OpenAI-compatible response.");

    public static OpenAICompatibleClientException StreamError(string message) =>
        new($"The endpoint reported an error: {message}");
}

/// <summary>Stateless HTTP boundary for OpenAI-compatible servers.</summary>
public sealed class OpenAICompatibleApiClient : IDisposable
{
    public static readonly TimeSpan ModelListTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Longest silence tolerated from a generation or preload request.</summary>
    public static readonly TimeSpan GenerationTimeout = TimeSpan.FromSeconds(120);

    private readonly HttpClient _http;

    public OpenAICompatibleApiClient(HttpMessageHandler? handler = null)
    {
        // Each request carries its own timeout, so the client-wide 100 second default must not win.
        _http = new HttpClient(handler ?? new SocketsHttpHandler()) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public void Dispose() => _http.Dispose();

    /// <summary>Lists the endpoint's models sorted by identifier.</summary>
    public Task<IReadOnlyList<OpenAICompatibleModelOption>> FetchModelsAsync(
        OpenAICompatibleEndpointConfiguration configuration,
        string? apiKey,
        CancellationToken ct = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, configuration.ApiUrl("models"));
        return SendAsync<IReadOnlyList<OpenAICompatibleModelOption>>(request, apiKey, ModelListTimeout, async (response, timeout) =>
        {
            await using var body = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            try
            {
                var payload = await JsonSerializer
                    .DeserializeAsync<ModelListResponse>(body, OpenAICompatibleJson.Options, timeout.Token)
                    .ConfigureAwait(false);
                return (payload ?? throw OpenAICompatibleClientException.MalformedResponse()).Data
                    .OrderBy(model => model.Id, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();
            }
            catch (JsonException)
            {
                throw OpenAICompatibleClientException.MalformedResponse();
            }
        }, ct);
    }

    /// <summary>
    /// Loads the selected model through Ollama's native API. Returns false without sending
    /// anything when the endpoint is not the default local Ollama server.
    /// </summary>
    public async Task<bool> PreloadDefaultOllamaModelAsync(
        OpenAICompatibleEndpointConfiguration configuration,
        string? apiKey,
        CancellationToken ct = default)
    {
        RequireModelName(configuration);
        if (configuration.DefaultOllamaGenerateUrl is not { } url) return false;

        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonBody(new OllamaPreloadRequest(configuration.ModelName, Prompt: "", Stream: false, KeepAlive: -1)),
        };
        return await SendAsync(request, apiKey, GenerationTimeout, (_, _) => Task.FromResult(true), ct)
            .ConfigureAwait(false);
    }

    /// <summary>Streams one generation and returns the accumulated raw text.</summary>
    /// <param name="onPartialRawText">Receives the accumulated raw text after each text event, on the reading thread.</param>
    public async Task<string> GenerateAsync(
        OpenAICompatibleEndpointConfiguration configuration,
        string? apiKey,
        string prompt,
        OpenAICompatibleGenerationOptions options,
        CancellationToken ct = default,
        Action<string>? onPartialRawText = null)
    {
        RequireModelName(configuration);

        var mode = configuration.ApiMode;
        var request = new HttpRequestMessage(HttpMethod.Post, configuration.ApiUrl(mode.Route()))
        {
            Content = JsonBody(mode == OpenAICompatibleApiMode.Completions
                ? new CompletionRequest(
                    configuration.ModelName, prompt, Stream: true,
                    options.MaxPredictionTokens, options.Temperature, options.TopP)
                : new ChatCompletionRequest(
                    configuration.ModelName,
                    [new ChatMessage("user", ChatInstruction + prompt)],
                    Stream: true,
                    options.MaxPredictionTokens, options.Temperature, options.TopP,
                    ReasoningEffort: "none")),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        return await SendAsync(request, apiKey, GenerationTimeout, async (response, timeout) =>
        {
            var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var accumulated = new StringBuilder();
            var assembler = new OpenAICompatibleSseAssembler(mode);
            var sawEvent = false;
            string? line;
            do
            {
                // The end of the stream is passed on as null, so a last event without its blank line still counts.
                line = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false);
                if (line is not null) timeout.CancelAfter(GenerationTimeout);
                if (assembler.Next(line) is not { } sseEvent) continue;
                switch (sseEvent.Kind)
                {
                    case OpenAICompatibleSseEventKind.Done:
                        return accumulated.ToString();
                    case OpenAICompatibleSseEventKind.Text:
                        sawEvent = true;
                        accumulated.Append(sseEvent.Value);
                        onPartialRawText?.Invoke(accumulated.ToString());
                        break;
                    case OpenAICompatibleSseEventKind.Error:
                        throw OpenAICompatibleClientException.StreamError(sseEvent.Value!);
                }
            }
            while (line is not null);

            // A stream that closes without [DONE] still produced a usable answer.
            return sawEvent ? accumulated.ToString() : throw OpenAICompatibleClientException.MalformedResponse();
        }, ct).ConfigureAwait(false);
    }

    // The shared prompt is a base-model continuation that ends at the caret. Chat models tend to
    // answer by repeating that final line, so the single user message states the expected output.
    private const string ChatInstruction =
        "Continue the text at the end of the context. Reply with only new continuation " +
        "text; do not repeat or quote existing text.\n\n";

    private static void RequireModelName(OpenAICompatibleEndpointConfiguration configuration)
    {
        if (configuration.ModelName.Length == 0)
        {
            throw new OpenAICompatibleEndpointException(OpenAICompatibleEndpointError.EmptyModelName);
        }
    }

    private static ByteArrayContent JsonBody(object payload)
    {
        var content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(payload, OpenAICompatibleJson.Options));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return content;
    }

    /// <summary>
    /// Sends with bearer authorization and an idle timeout that <paramref name="read"/> may
    /// restart. A timeout surfaces as <see cref="TimeoutException"/>, caller cancellation as
    /// <see cref="OperationCanceledException"/>.
    /// </summary>
    private async Task<T> SendAsync<T>(
        HttpRequestMessage request,
        string? apiKey,
        TimeSpan idleTimeout,
        Func<HttpResponseMessage, CancellationTokenSource, Task<T>> read,
        CancellationToken ct)
    {
        using var _ = request;
        if (apiKey?.Trim() is { Length: > 0 } key)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(idleTimeout);
        try
        {
            using var response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                string? detail = await ReadErrorDetailAsync(response, apiKey, timeout.Token).ConfigureAwait(false);
                throw OpenAICompatibleClientException.Server((int)response.StatusCode, detail);
            }
            return await read(response, timeout).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("The request timed out.");
        }
    }

    /// <summary>Longest server explanation shown with a failed status.</summary>
    private const int ErrorDetailLength = 200;

    /// <summary>Bytes of a failed response read for its explanation.</summary>
    private const int ErrorBodyBytes = 8 * 1024;

    /// <summary>
    /// Longest wait for a failed response's explanation. The status is already known, so a body
    /// that stalls must not hold it back until the request's own timeout.
    /// </summary>
    private static readonly TimeSpan ErrorBodyTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// The server's explanation of a failed status, or null when the body has none worth
    /// showing. A failure to read it only loses the explanation. The API key is redacted, since
    /// some servers echo the credential they rejected.
    /// </summary>
    private static async Task<string?> ReadErrorDetailAsync(
        HttpResponseMessage response, string? apiKey, CancellationToken ct)
    {
        string body;
        using var bodyTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bodyTimeout.CancelAfter(ErrorBodyTimeout);
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(bodyTimeout.Token).ConfigureAwait(false);
            var buffer = new byte[ErrorBodyBytes];
            int length = 0;
            while (length < buffer.Length
                && await stream.ReadAsync(buffer.AsMemory(length), bodyTimeout.Token).ConfigureAwait(false) is var read and > 0)
            {
                length += read;
            }
            body = Encoding.UTF8.GetString(buffer, 0, length);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException
                                   || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            return null;
        }

        string? detail = ErrorDetail(body);
        if (detail is null) return null;
        if (apiKey?.Trim() is { Length: > 0 } key) detail = detail.Replace(key, "[API key]", StringComparison.Ordinal);
        detail = string.Join(' ', detail.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return detail.Length > ErrorDetailLength ? detail[..ErrorDetailLength].TrimEnd() + "…" : detail;
    }

    /// <summary>
    /// The message in the error shapes OpenAI-compatible servers use, or plain text. An HTML page
    /// from a proxy says nothing the status does not.
    /// </summary>
    private static string? ErrorDetail(string body)
    {
        var trimmed = body.Trim();
        if (trimmed.Length == 0 || trimmed.StartsWith('<')) return null;
        if (!trimmed.StartsWith('{')) return trimmed;
        try
        {
            using var document = JsonDocument.Parse(trimmed);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (root.TryGetProperty("error", out var error))
            {
                if (error.ValueKind == JsonValueKind.String) return NonEmpty(error.GetString());
                if (error.ValueKind == JsonValueKind.Object
                    && error.TryGetProperty("message", out var nested)
                    && nested.ValueKind == JsonValueKind.String)
                {
                    return NonEmpty(nested.GetString());
                }
            }
            foreach (var name in (ReadOnlySpan<string>)["message", "detail"])
            {
                if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                {
                    return NonEmpty(value.GetString());
                }
            }
            return null;
        }
        catch (JsonException)
        {
            return null;
        }

        static string? NonEmpty(string? text) => string.IsNullOrWhiteSpace(text) ? null : text;
    }
}

public enum OpenAICompatibleSseEventKind
{
    Ignore,
    Done,
    Text,
    Error,
}

public readonly record struct OpenAICompatibleSseEvent(OpenAICompatibleSseEventKind Kind, string? Value = null)
{
    public static readonly OpenAICompatibleSseEvent Ignore = new(OpenAICompatibleSseEventKind.Ignore);
    public static readonly OpenAICompatibleSseEvent Done = new(OpenAICompatibleSseEventKind.Done);
    public static OpenAICompatibleSseEvent Text(string text) => new(OpenAICompatibleSseEventKind.Text, text);
    public static OpenAICompatibleSseEvent Error(string message) => new(OpenAICompatibleSseEventKind.Error, message);
}

/// <summary>Pure line decoder for OpenAI's data-only SSE streams.</summary>
public static class OpenAICompatibleSseDecoder
{
    internal static readonly string[] MetadataPrefixes = [":", "event:", "id:", "retry:"];

    /// <exception cref="OpenAICompatibleClientException">The payload is not a stream chunk.</exception>
    public static OpenAICompatibleSseEvent Decode(string line, OpenAICompatibleApiMode mode)
    {
        var payload = line.Trim();
        if (payload.Length == 0 || MetadataPrefixes.Any(prefix => payload.StartsWith(prefix, StringComparison.Ordinal)))
        {
            return OpenAICompatibleSseEvent.Ignore;
        }
        if (payload.StartsWith("data:", StringComparison.Ordinal))
        {
            payload = payload["data:".Length..].TrimStart();
            // Some servers and proxies send an empty data line as a keep-alive. It carries no
            // chunk, and treating it as malformed would end an otherwise healthy stream.
            if (payload.Length == 0) return OpenAICompatibleSseEvent.Ignore;
        }
        return DecodePayload(payload, mode);
    }

    /// <summary>Decodes one event's data, with any <c>data:</c> prefixes already removed.</summary>
    /// <exception cref="OpenAICompatibleClientException">The payload is not a stream chunk.</exception>
    public static OpenAICompatibleSseEvent DecodePayload(string payload, OpenAICompatibleApiMode mode) =>
        TryDecodePayload(payload, mode, out var sseEvent)
            ? sseEvent
            : throw OpenAICompatibleClientException.MalformedResponse();

    /// <summary>
    /// Like <see cref="DecodePayload"/>, but false instead of throwing, so a caller can tell an
    /// event that is still incomplete from one that is whole.
    /// </summary>
    public static bool TryDecodePayload(string payload, OpenAICompatibleApiMode mode, out OpenAICompatibleSseEvent sseEvent)
    {
        payload = payload.Trim();
        sseEvent = OpenAICompatibleSseEvent.Ignore;
        if (payload == "[DONE]")
        {
            sseEvent = OpenAICompatibleSseEvent.Done;
            return true;
        }

        StreamChunk? chunk;
        try
        {
            chunk = JsonSerializer.Deserialize<StreamChunk>(payload, OpenAICompatibleJson.Options);
        }
        catch (JsonException)
        {
            return false;
        }
        if (chunk is null) return false;

        switch (chunk.Error)
        {
            // OpenAI nests the message in an object. Ollama and others send a plain string.
            case { ValueKind: JsonValueKind.String } error:
                if (error.GetString() is { Length: > 0 } message)
                {
                    sseEvent = OpenAICompatibleSseEvent.Error(message);
                    return true;
                }
                break;
            case { ValueKind: JsonValueKind.Object } error:
                if (!error.TryGetProperty("message", out var nested) || nested.ValueKind != JsonValueKind.String)
                {
                    return false;
                }
                if (nested.GetString() is { Length: > 0 } nestedMessage)
                {
                    sseEvent = OpenAICompatibleSseEvent.Error(nestedMessage);
                    return true;
                }
                break;
            case null or { ValueKind: JsonValueKind.Null }:
                break;
            default:
                return false;
        }

        if (chunk.Choices is not [{ } choice, ..]) return true;
        var text = mode == OpenAICompatibleApiMode.Completions
            ? choice.Text
            : choice.Delta?.Content ?? choice.Message?.Content;
        if (!string.IsNullOrEmpty(text)) sseEvent = OpenAICompatibleSseEvent.Text(text);
        return true;
    }
}

/// <summary>
/// Joins a stream's lines into events. Following the SSE format, consecutive <c>data:</c> lines
/// form one event that a blank line ends. Most servers send one line per event, so a payload is
/// decoded as soon as it is whole rather than at the blank line. Lines without a field name are
/// taken as data too, which also reads a server that ignored streaming and sent one JSON body.
/// </summary>
public sealed class OpenAICompatibleSseAssembler(OpenAICompatibleApiMode mode)
{
    private readonly StringBuilder _pending = new();

    /// <summary>
    /// Takes the next line, or null at the end of the stream, and returns the event it completes.
    /// </summary>
    /// <exception cref="OpenAICompatibleClientException">An event ended that is not a stream chunk.</exception>
    public OpenAICompatibleSseEvent? Next(string? line)
    {
        var trimmed = line?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            // A blank line or the end of the stream ends the event, which has to be whole by now.
            if (_pending.Length == 0) return null;
            var payload = _pending.ToString();
            _pending.Clear();
            return OpenAICompatibleSseDecoder.DecodePayload(payload, mode);
        }
        if (OpenAICompatibleSseDecoder.MetadataPrefixes.Any(prefix => trimmed.StartsWith(prefix, StringComparison.Ordinal))) return null;

        var data = trimmed.StartsWith("data:", StringComparison.Ordinal) ? trimmed["data:".Length..].TrimStart() : trimmed;
        // Some servers and proxies send an empty data line as a keep-alive.
        if (_pending.Length == 0 && data.Length == 0) return null;
        if (_pending.Length > 0) _pending.Append('\n');
        _pending.Append(data);

        if (!OpenAICompatibleSseDecoder.TryDecodePayload(_pending.ToString(), mode, out var sseEvent)) return null;
        _pending.Clear();
        return sseEvent;
    }
}

internal static class OpenAICompatibleJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
    };
}

internal sealed record ModelListResponse(IReadOnlyList<OpenAICompatibleModelOption> Data);

// Ollama treats an empty, non-streaming generate request as a model load. A keep_alive of -1
// keeps the weights resident until Ollama stops or is asked to unload them.
internal sealed record OllamaPreloadRequest(string Model, string Prompt, bool Stream, int KeepAlive);

internal sealed record CompletionRequest(
    string Model,
    string Prompt,
    bool Stream,
    int MaxTokens,
    double Temperature,
    double TopP);

internal sealed record ChatMessage(string Role, string Content);

// A reasoning_effort of none keeps reasoning-capable models from spending the short token budget
// on an invisible thinking channel. Models without reasoning ignore the field.
internal sealed record ChatCompletionRequest(
    string Model,
    IReadOnlyList<ChatMessage> Messages,
    bool Stream,
    int MaxTokens,
    double Temperature,
    double TopP,
    string ReasoningEffort);

// The error is an object with a message or a plain string, depending on the server.
internal sealed record StreamChunk(IReadOnlyList<StreamChoice?>? Choices = null, JsonElement? Error = null);

internal sealed record StreamChoice(string? Text = null, StreamContent? Delta = null, StreamContent? Message = null);

internal sealed record StreamContent(string? Content = null);