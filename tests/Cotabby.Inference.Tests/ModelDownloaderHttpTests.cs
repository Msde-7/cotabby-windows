using System.Net;
using System.Net.Sockets;
using Cotabby.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cotabby.Inference.Tests;

/// <summary>Downloads over real sockets from a local HttpListener, with and without range support.</summary>
[Collection(ModelDirectoryCollection.Name)]
public sealed class ModelDownloaderHttpTests : IDisposable
{
    private static readonly byte[] File256K = [.. "GGUF"u8, .. Enumerable.Range(0, (256 << 10) - 4).Select(i => (byte)(i * 7))];

    private readonly ModelDirectory _models = new();
    private readonly HttpListener _listener = new();
    private readonly string _url;
    private int _rangeRequests;
    private int _fullRequests;

    public ModelDownloaderHttpTests()
    {
        int port;
        using (var probe = new TcpListener(IPAddress.Loopback, 0))
        {
            probe.Start();
            port = ((IPEndPoint)probe.LocalEndpoint).Port;
        }
        _url = $"http://localhost:{port}/";
        _listener.Prefixes.Add(_url);
        _listener.Start();
    }

    public void Dispose()
    {
        _listener.Close();
        _models.Dispose();
    }

    private CotabbyModel Model => new()
    {
        Id = "http",
        DisplayName = "http",
        DownloadUrl = _url + "model.gguf",
        ApproxSizeBytes = File256K.Length,
        ContextLength = 4096,
    };

    private async Task ServeAsync(bool ranges)
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException)
            {
                return;
            }
            _ = Task.Run(() => Answer(context, ranges));
        }
    }

    private void Answer(HttpListenerContext context, bool ranges)
    {
        var response = context.Response;
        try
        {
            long from = 0, to = File256K.Length - 1;
            string? range = context.Request.Headers["Range"];
            if (ranges && range is not null && range.StartsWith("bytes=", StringComparison.Ordinal))
            {
                var bounds = range["bytes=".Length..].Split('-');
                from = long.Parse(bounds[0]);
                if (bounds[1].Length > 0) to = Math.Min(long.Parse(bounds[1]), to);
                response.StatusCode = 206;
                response.AddHeader("Content-Range", $"bytes {from}-{to}/{File256K.Length}");
                Interlocked.Increment(ref _rangeRequests);
            }
            else
            {
                Interlocked.Increment(ref _fullRequests);
            }
            response.ContentLength64 = to - from + 1;
            response.OutputStream.Write(File256K, (int)from, (int)(to - from + 1));
            response.Close();
        }
        catch (HttpListenerException)
        {
            response.Abort();
        }
    }

    private Task Download() =>
        new ModelDownloader(new HttpClient(), NullLogger<ModelDownloader>.Instance)
        {
            Connections = 8,
            SegmentedMinimumBytes = 1,
            RetryDelay = TimeSpan.Zero,
        }.DownloadAsync(Model, progress: null, CancellationToken.None);

    [Fact]
    public async Task Segmented_DownloadsOverEightRangesFromARealServer()
    {
        _ = ServeAsync(ranges: true);

        await Download();

        Assert.Equal(File256K, File.ReadAllBytes(ModelDownloader.LocalPath(Model)));
        Assert.Equal(8, _rangeRequests);
        Assert.Equal(0, _fullRequests);
    }

    [Fact]
    public async Task Segmented_FallsBackToOneStreamFromAServerWithoutRanges()
    {
        _ = ServeAsync(ranges: false);

        await Download();

        Assert.Equal(File256K, File.ReadAllBytes(ModelDownloader.LocalPath(Model)));
        Assert.Equal(0, _rangeRequests);
        Assert.Equal(2, _fullRequests);
    }
}
