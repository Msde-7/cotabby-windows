using Cotabby.Core.Suggestions;

namespace Cotabby.Core.Tests.Suggestions;

public sealed class SuggestionEngineRouterTests
{
    private static readonly SuggestionRequest Request =
        SuggestionRequestFactory.Build(Fields.Text("hello"), "request", SuggestionSettings.Default);

    private readonly ScriptedEngine _local = new() { Completion = _ => " local" };
    private readonly ScriptedEngine _endpoint = new() { Completion = _ => " endpoint" };
    private readonly SuggestionEngineRouter _router;

    public SuggestionEngineRouterTests() => _router = new SuggestionEngineRouter(_local, _endpoint);

    private Task<string> Generate() => _router.GenerateAsync(Request, CancellationToken.None);

    [Fact]
    public async Task GenerateAsync_UsesTheLocalEngineByDefault()
    {
        Assert.Equal(" local", await Generate());
        Assert.Empty(_endpoint.Requests);
    }

    [Fact]
    public async Task GenerateAsync_UsesOnlyTheEndpointOnceSelected()
    {
        _router.UseEndpoint = true;

        Assert.Equal(" endpoint", await Generate());
        Assert.Empty(_local.Requests);
    }

    [Fact]
    public void IsReady_FollowsTheSelectedEngineWithNoFallback()
    {
        _endpoint.IsReady = false;
        Assert.True(_router.IsReady);

        _router.UseEndpoint = true;
        Assert.False(_router.IsReady);

        _endpoint.IsReady = true;
        _local.IsReady = false;
        Assert.True(_router.IsReady);
    }
}
