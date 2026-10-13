using System.Globalization;

namespace Cotabby.Inference.OpenAICompatible;

/// <summary>Which OpenAI-compatible text-generation route Cotabby calls.</summary>
public enum OpenAICompatibleApiMode
{
    Completions,
    ChatCompletions,
}

public static class OpenAICompatibleApiModeExtensions
{
    /// <summary>Route relative to the base URL.</summary>
    public static string Route(this OpenAICompatibleApiMode mode) => mode switch
    {
        OpenAICompatibleApiMode.Completions => "completions",
        _ => "chat/completions",
    };
}

/// <summary>One model identifier returned by <c>GET /v1/models</c>.</summary>
public sealed record OpenAICompatibleModelOption(string Id);

/// <summary>Chooses the durable model identifier after endpoint discovery finishes.</summary>
public static class OpenAICompatibleModelSelectionResolver
{
    /// <summary>
    /// Keeps a still-available choice and otherwise picks the first discovered model. An empty
    /// catalog returns null so manual identifiers survive endpoints that cannot list models.
    /// </summary>
    public static string? PreferredSelection(
        string currentSelection,
        IReadOnlyList<OpenAICompatibleModelOption> discoveredModels)
    {
        if (discoveredModels.Count == 0) return null;
        var current = currentSelection.Trim();
        return discoveredModels.Any(model => model.Id == current) ? current : discoveredModels[0].Id;
    }
}

/// <summary>Privacy classification for the configured server host.</summary>
public enum OpenAICompatibleHostScope
{
    Loopback,
    LocalNetwork,
    PublicInternet,
}

/// <summary>The non-secret endpoint values the user edits in Settings.</summary>
public sealed record OpenAICompatibleEndpointSettings(
    string BaseUrl,
    string ModelName,
    OpenAICompatibleApiMode ApiMode);

/// <summary>
/// A validated base URL plus the endpoint-specific model and request mode. Discovery and
/// generation both build this value first, so they can never normalize a URL differently.
/// </summary>
public sealed record OpenAICompatibleEndpointConfiguration
{
    public const string DefaultBaseUrl = "http://127.0.0.1:11434/v1";

    public Uri BaseUrl { get; }
    public string ModelName { get; }
    public OpenAICompatibleApiMode ApiMode { get; }
    public OpenAICompatibleHostScope HostScope { get; }

    public OpenAICompatibleEndpointConfiguration(OpenAICompatibleEndpointSettings settings)
        : this(settings.BaseUrl, settings.ModelName, settings.ApiMode)
    {
    }

    /// <exception cref="OpenAICompatibleEndpointException">The base URL breaks the URL policy.</exception>
    public OpenAICompatibleEndpointConfiguration(
        string baseUrl,
        string modelName,
        OpenAICompatibleApiMode apiMode)
    {
        var candidate = baseUrl.Trim();
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https")
            || uri.Host.Length == 0
            || uri.UserInfo.Length > 0
            || candidate.AsSpan().IndexOfAny('?', '#') >= 0)
        {
            throw new OpenAICompatibleEndpointException(OpenAICompatibleEndpointError.InvalidBaseUrl);
        }

        var scope = HostScopeFor(uri.Host);
        if (uri.Scheme == "http" && scope == OpenAICompatibleHostScope.PublicInternet)
        {
            throw new OpenAICompatibleEndpointException(OpenAICompatibleEndpointError.InsecurePublicHttp);
        }

        var path = uri.AbsolutePath.TrimEnd('/');
        BaseUrl = new Uri(uri.GetLeftPart(UriPartial.Authority) + (path.Length == 0 ? "/v1" : path));
        ModelName = modelName.Trim();
        ApiMode = apiMode;
        HostScope = scope;
    }

    public Uri ApiUrl(string path) => new($"{BaseUrl.AbsoluteUri}/{path}");

    /// <summary>
    /// Ollama's preload API lives outside its <c>/v1</c> namespace. Only the exact default local
    /// Ollama address gets this URL, so LM Studio, vLLM and hosted endpoints never receive
    /// Ollama-only requests.
    /// </summary>
    public Uri? DefaultOllamaGenerateUrl => BaseUrl.AbsoluteUri == DefaultBaseUrl
        ? new Uri(BaseUrl.GetLeftPart(UriPartial.Authority) + "/api/generate")
        : null;

    public string? PrivacyWarning => HostScope switch
    {
        OpenAICompatibleHostScope.Loopback => null,
        OpenAICompatibleHostScope.LocalNetwork =>
            "Cotabby will send typed text and any enabled context to this server on your local network.",
        _ => "This server is outside your PC. Typed text and any enabled context will leave your device.",
    };

    public static OpenAICompatibleHostScope HostScopeFor(string rawHost)
    {
        var host = rawHost.ToLowerInvariant().Trim('[', ']');
        if (host == "localhost" || host.EndsWith(".localhost", StringComparison.Ordinal) || host == "::1")
        {
            return OpenAICompatibleHostScope.Loopback;
        }

        if (Ipv4Octets(host) is { } octets)
        {
            if (octets[0] == 127) return OpenAICompatibleHostScope.Loopback;
            return octets[0] == 10
                || (octets[0] == 172 && octets[1] is >= 16 and <= 31)
                || (octets[0] == 192 && octets[1] == 168)
                || (octets[0] == 169 && octets[1] == 254)
                ? OpenAICompatibleHostScope.LocalNetwork
                : OpenAICompatibleHostScope.PublicInternet;
        }

        // A single-label DNS name is not proof of LAN scope because DNS may resolve it publicly.
        // The private IPv6 prefixes only mean something on an address, which always has a colon.
        // Without that check a public name such as fdic.gov would pass as local.
        bool privateIpv6 = host.Contains(':')
            && (host.StartsWith("fc", StringComparison.Ordinal)
                || host.StartsWith("fd", StringComparison.Ordinal)
                || (host.StartsWith("fe", StringComparison.Ordinal) && host.Length > 2 && "89ab".Contains(host[2])));
        return host.EndsWith(".local", StringComparison.Ordinal) || privateIpv6
            ? OpenAICompatibleHostScope.LocalNetwork
            : OpenAICompatibleHostScope.PublicInternet;
    }

    private static int[]? Ipv4Octets(string host)
    {
        var pieces = host.Split('.');
        if (pieces.Length != 4) return null;
        var octets = new int[4];
        for (int i = 0; i < 4; i++)
        {
            if (!int.TryParse(pieces[i], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out octets[i])
                || octets[i] is < 0 or > 255)
            {
                return null;
            }
        }
        return octets;
    }
}

public enum OpenAICompatibleEndpointError
{
    InvalidBaseUrl,
    InsecurePublicHttp,
    EmptyModelName,
}

/// <summary>A configuration problem the user can fix in Settings. The message is user-facing.</summary>
public sealed class OpenAICompatibleEndpointException(OpenAICompatibleEndpointError error)
    : Exception(Describe(error))
{
    public OpenAICompatibleEndpointError Error { get; } = error;

    private static string Describe(OpenAICompatibleEndpointError error) => error switch
    {
        OpenAICompatibleEndpointError.InvalidBaseUrl =>
            "Enter an HTTP or HTTPS base URL without credentials, a query, or a fragment.",
        OpenAICompatibleEndpointError.InsecurePublicHttp =>
            "Public endpoints must use HTTPS. HTTP is limited to this PC or the local network.",
        _ => "Choose or enter a model before generating suggestions.",
    };
}

/// <summary>UI-facing state for the latest endpoint model-discovery request.</summary>
public abstract record OpenAICompatibleConnectionState
{
    public sealed record Idle : OpenAICompatibleConnectionState;
    public sealed record Connecting : OpenAICompatibleConnectionState;
    public sealed record Ready(int ModelCount) : OpenAICompatibleConnectionState;
    public sealed record Failed(string Message) : OpenAICompatibleConnectionState;

    public string Summary => this switch
    {
        Idle => "Not connected",
        Connecting => "Connecting…",
        Ready { ModelCount: 1 } => "Connected · 1 model",
        Ready ready => $"Connected · {ready.ModelCount} models",
        _ => ((Failed)this).Message,
    };
}

/// <summary>Narrow credential boundary so the app uses DPAPI while tests use an in-memory fake.</summary>
public interface IOpenAICompatibleCredentialStore
{
    string? ReadApiKey();

    /// <summary>Stores the trimmed key. A null or blank key deletes the stored one.</summary>
    void SaveApiKey(string? apiKey);

    void DeleteApiKey();
}
