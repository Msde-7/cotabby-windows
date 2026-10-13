using Cotabby.Inference.OpenAICompatible;

namespace Cotabby.Inference.Tests;

public class OpenAICompatibleEndpointConfigurationTests
{
    private static OpenAICompatibleEndpointConfiguration Configuration(
        string baseUrl = OpenAICompatibleEndpointConfiguration.DefaultBaseUrl,
        string modelName = "m") =>
        new(baseUrl, modelName, OpenAICompatibleApiMode.Completions);

    [Theory]
    [InlineData("localhost", OpenAICompatibleHostScope.Loopback)]
    [InlineData("LOCALHOST", OpenAICompatibleHostScope.Loopback)]
    [InlineData("api.localhost", OpenAICompatibleHostScope.Loopback)]
    [InlineData("::1", OpenAICompatibleHostScope.Loopback)]
    [InlineData("[::1]", OpenAICompatibleHostScope.Loopback)]
    [InlineData("127.0.0.1", OpenAICompatibleHostScope.Loopback)]
    [InlineData("127.8.9.10", OpenAICompatibleHostScope.Loopback)]
    [InlineData("10.0.0.5", OpenAICompatibleHostScope.LocalNetwork)]
    [InlineData("172.16.0.1", OpenAICompatibleHostScope.LocalNetwork)]
    [InlineData("172.31.255.255", OpenAICompatibleHostScope.LocalNetwork)]
    [InlineData("192.168.1.50", OpenAICompatibleHostScope.LocalNetwork)]
    [InlineData("169.254.10.10", OpenAICompatibleHostScope.LocalNetwork)]
    [InlineData("ollama.local", OpenAICompatibleHostScope.LocalNetwork)]
    [InlineData("fd12:3456::1", OpenAICompatibleHostScope.LocalNetwork)]
    [InlineData("fd00::1", OpenAICompatibleHostScope.LocalNetwork)]
    [InlineData("fc00::1", OpenAICompatibleHostScope.LocalNetwork)]
    [InlineData("fe80::1", OpenAICompatibleHostScope.LocalNetwork)]
    // Just outside the RFC 1918 172.16/12 and 192.168/16 blocks.
    [InlineData("172.15.0.1", OpenAICompatibleHostScope.PublicInternet)]
    [InlineData("172.32.0.1", OpenAICompatibleHostScope.PublicInternet)]
    [InlineData("192.169.1.50", OpenAICompatibleHostScope.PublicInternet)]
    [InlineData("8.8.8.8", OpenAICompatibleHostScope.PublicInternet)]
    // Not a valid IPv4 literal, so it is treated as a DNS name.
    [InlineData("256.1.1.1", OpenAICompatibleHostScope.PublicInternet)]
    [InlineData("fec0::1", OpenAICompatibleHostScope.PublicInternet)]
    [InlineData("2001:4860:4860::8888", OpenAICompatibleHostScope.PublicInternet)]
    [InlineData("models.example.com", OpenAICompatibleHostScope.PublicInternet)]
    // A single-label name may resolve publicly, so it is not assumed to be on the LAN.
    [InlineData("internal-llm", OpenAICompatibleHostScope.PublicInternet)]
    public void HostScope_ClassifiesLoopbackPrivateAndPublicHosts(string host, OpenAICompatibleHostScope expected) =>
        Assert.Equal(expected, OpenAICompatibleEndpointConfiguration.HostScopeFor(host));

    [Fact]
    public void Constructor_NormalizesSchemeTrailingSlashesAndModelName()
    {
        var configuration = Configuration("HTTPS://models.example.com/custom/v1///", "  gemma4  ");

        Assert.Equal("https://models.example.com/custom/v1", configuration.BaseUrl.AbsoluteUri);
        Assert.Equal("gemma4", configuration.ModelName);
        Assert.Equal(OpenAICompatibleApiMode.Completions, configuration.ApiMode);
        Assert.Equal(OpenAICompatibleHostScope.PublicInternet, configuration.HostScope);
        Assert.Equal("llama3:8b", Configuration(modelName: "  llama3:8b \n").ModelName);
    }

    [Fact]
    public void Constructor_NormalizesLoopbackLanAndPublicBaseUrls()
    {
        var loopback = Configuration(" http://127.0.0.1:11434/ ");
        Assert.Equal("http://127.0.0.1:11434/v1", loopback.BaseUrl.AbsoluteUri);
        Assert.Equal("http://127.0.0.1:11434/v1/models", loopback.ApiUrl("models").AbsoluteUri);
        Assert.Equal("http://127.0.0.1:11434/api/generate", loopback.DefaultOllamaGenerateUrl?.AbsoluteUri);
        Assert.Equal(OpenAICompatibleHostScope.Loopback, loopback.HostScope);
        Assert.Null(loopback.PrivacyWarning);

        var lan = Configuration("http://192.168.1.50:8000/v1/");
        Assert.Equal("http://192.168.1.50:8000/v1", lan.BaseUrl.AbsoluteUri);
        Assert.Null(lan.DefaultOllamaGenerateUrl);
        Assert.Equal(OpenAICompatibleHostScope.LocalNetwork, lan.HostScope);
        Assert.Equal(
            "Cotabby will send typed text and any enabled context to this server on your local network.",
            lan.PrivacyWarning);

        var hosted = Configuration("https://models.example.com/custom/v1");
        Assert.Equal("https://models.example.com/custom/v1", hosted.BaseUrl.AbsoluteUri);
        Assert.Equal(OpenAICompatibleHostScope.PublicInternet, hosted.HostScope);
        Assert.Equal(
            "This server is outside your PC. Typed text and any enabled context will leave your device.",
            hosted.PrivacyWarning);
    }

    // The native preload route is gated on the exact default string, so every spelling of the
    // default Ollama root must normalize to it.
    [Theory]
    [InlineData("http://127.0.0.1:11434")]
    [InlineData("HTTP://127.0.0.1:11434/v1")]
    [InlineData("http://127.0.0.1:11434/v1///")]
    public void DefaultOllamaGenerateUrl_IsOfferedForEverySpellingOfTheDefault(string spelling)
    {
        var configuration = Configuration(spelling);

        Assert.Equal(OpenAICompatibleEndpointConfiguration.DefaultBaseUrl, configuration.BaseUrl.AbsoluteUri);
        Assert.Equal("http://127.0.0.1:11434/api/generate", configuration.DefaultOllamaGenerateUrl?.AbsoluteUri);
    }

    [Fact]
    public void DefaultOllamaGenerateUrl_IsNotOfferedForAnotherLoopbackPort() =>
        Assert.Null(Configuration("http://127.0.0.1:1234/v1").DefaultOllamaGenerateUrl);

    [Theory]
    [InlineData("http://models.example.com/v1")]
    [InlineData("http://internal-llm:11434/v1")]
    [InlineData("http://[2001:4860:4860::8888]/v1")]
    public void Constructor_RejectsInsecurePublicHttp(string baseUrl)
    {
        var ex = Assert.Throws<OpenAICompatibleEndpointException>(() => Configuration(baseUrl));
        Assert.Equal(OpenAICompatibleEndpointError.InsecurePublicHttp, ex.Error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("localhost:11434")]
    [InlineData("file:///tmp/model")]
    [InlineData("ftp://127.0.0.1/v1")]
    [InlineData("https://host/v1?token=secret")]
    [InlineData("http://127.0.0.1:11434/v1#fragment")]
    [InlineData("http://user:password@127.0.0.1:11434/v1")]
    [InlineData("https://user:secret@models.example.com/v1")]
    public void Constructor_RejectsInvalidComponents(string baseUrl)
    {
        var ex = Assert.Throws<OpenAICompatibleEndpointException>(() => Configuration(baseUrl));
        Assert.Equal(OpenAICompatibleEndpointError.InvalidBaseUrl, ex.Error);
    }

    [Fact]
    public void ApiMode_RoutesAreRelativeToTheBaseUrl()
    {
        Assert.Equal("completions", OpenAICompatibleApiMode.Completions.Route());
        Assert.Equal("chat/completions", OpenAICompatibleApiMode.ChatCompletions.Route());
    }

    [Fact]
    public void ConnectionState_SummaryPluralizesModelCountAndExposesFailureDetail()
    {
        Assert.Equal("Not connected", new OpenAICompatibleConnectionState.Idle().Summary);
        Assert.Equal("Connecting…", new OpenAICompatibleConnectionState.Connecting().Summary);
        Assert.Equal("Connected · 1 model", new OpenAICompatibleConnectionState.Ready(1).Summary);
        Assert.Equal("Connected · 3 models", new OpenAICompatibleConnectionState.Ready(3).Summary);
        Assert.Equal("Timed out", new OpenAICompatibleConnectionState.Failed("Timed out").Summary);

        Assert.Equal("Timed out", new OpenAICompatibleConnectionState.Failed("Timed out").FailureDetail);
        Assert.Null(new OpenAICompatibleConnectionState.Ready(2).FailureDetail);
        Assert.Null(new OpenAICompatibleConnectionState.Idle().FailureDetail);
    }

    [Fact]
    public void EndpointErrors_HaveDistinctUserFacingDescriptions()
    {
        var descriptions = Enum.GetValues<OpenAICompatibleEndpointError>()
            .Select(OpenAICompatibleEndpointException.Describe)
            .ToHashSet();

        Assert.Equal(3, descriptions.Count);
        Assert.Equal(
            "Public endpoints must use HTTPS. HTTP is limited to this PC or the local network.",
            new OpenAICompatibleEndpointException(OpenAICompatibleEndpointError.InsecurePublicHttp).Message);
    }

    [Theory]
    [InlineData("", "alpha")]
    [InlineData("beta", "beta")]
    [InlineData("  beta\n", "beta")]
    [InlineData("removed-model", "alpha")]
    public void ModelSelectionResolver_ReconcilesSavedSelectionWithDiscoveredCatalog(string current, string expected)
    {
        OpenAICompatibleModelOption[] models = [new("alpha"), new("beta")];

        Assert.Equal(expected, OpenAICompatibleModelSelectionResolver.PreferredSelection(current, models));
    }

    [Fact]
    public void ModelSelectionResolver_LeavesAManualIdentifierAloneWhenTheCatalogIsEmpty() =>
        Assert.Null(OpenAICompatibleModelSelectionResolver.PreferredSelection("manual-model", []));
}
