using System.Net;
using Cotabby.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cotabby.Inference.Tests;

[Collection(ModelDirectoryCollection.Name)]
public sealed class ModelDownloaderTests : IDisposable
{
    private static readonly byte[] Gguf = [.. "GGUF"u8, 1, 2, 3, 4, 5, 6, 7, 8];

    private readonly ModelDirectory _models = new();
    private readonly CotabbyModel _model = new()
    {
        Id = "test",
        DisplayName = "test",
        DownloadUrl = "https://example.test/models/test.gguf",
        ApproxSizeBytes = Gguf.Length,
        ContextLength = 4096,
    };

    public void Dispose() => _models.Dispose();

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<long?> RangeStarts { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            RangeStarts.Add(request.Headers.Range?.Ranges.First().From);
            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage Body(HttpStatusCode status, byte[] bytes, long? declaredLength = null)
    {
        var content = new ByteArrayContent(bytes);
        if (declaredLength is { } length) content.Headers.ContentLength = length;
        return new HttpResponseMessage(status) { Content = content };
    }

    private Task Download(Handler handler) =>
        new ModelDownloader(new HttpClient(handler), NullLogger<ModelDownloader>.Instance)
            .DownloadAsync(_model, progress: null, CancellationToken.None);

    private string FinalPath => ModelDownloader.LocalPath(_model);

    private void WritePartial(byte[] bytes) => _models.Write("test.gguf.part", bytes);

    [Fact]
    public async Task Download_WritesTheFileAndMarksItCached()
    {
        Assert.False(ModelDownloader.IsCached(_model));

        await Download(new Handler(_ => Body(HttpStatusCode.OK, Gguf)));

        Assert.Equal(Gguf, File.ReadAllBytes(FinalPath));
        Assert.True(ModelDownloader.IsCached(_model));
        Assert.False(File.Exists(FinalPath + ".part"));
    }

    [Fact]
    public async Task Download_ResumesAPartialFileWithARangeRequest()
    {
        WritePartial(Gguf[..5]);
        var handler = new Handler(_ => Body(HttpStatusCode.PartialContent, Gguf[5..]));

        await Download(handler);

        Assert.Equal([5L], handler.RangeStarts);
        Assert.Equal(Gguf, File.ReadAllBytes(FinalPath));
    }

    [Fact]
    public async Task Download_StartsOverWhenTheServerIgnoresTheRange()
    {
        WritePartial(Gguf[..5]);

        await Download(new Handler(_ => Body(HttpStatusCode.OK, Gguf)));

        Assert.Equal(Gguf, File.ReadAllBytes(FinalPath));
    }

    [Fact]
    public async Task Download_PromotesAPartialFileThatIsAlreadyComplete()
    {
        WritePartial(Gguf);

        await Download(new Handler(_ => new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable)));

        Assert.True(ModelDownloader.IsCached(_model));
    }

    [Fact]
    public async Task Download_KeepsATruncatedPartialForTheNextAttempt()
    {
        var handler = new Handler(_ => Body(HttpStatusCode.OK, Gguf[..6], declaredLength: Gguf.Length));

        await Assert.ThrowsAnyAsync<Exception>(() => Download(handler));

        Assert.False(ModelDownloader.IsCached(_model));
        Assert.True(File.Exists(FinalPath + ".part"));
    }

    [Fact]
    public async Task Download_RejectsAFileThatIsNotAModel()
    {
        var handler = new Handler(_ => Body(HttpStatusCode.OK, "<html>login</html>"u8.ToArray()));

        await Assert.ThrowsAsync<InvalidDataException>(() => Download(handler));

        Assert.False(ModelDownloader.IsCached(_model));
        Assert.False(File.Exists(FinalPath + ".part"));
    }

    [Fact]
    public async Task Download_DoesNothingWhenTheModelIsCached()
    {
        _models.Write("test.gguf", Gguf);
        var handler = new Handler(_ => throw new InvalidOperationException("no request expected"));

        await Download(handler);

        Assert.Empty(handler.RangeStarts);
    }
}
