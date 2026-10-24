using System.Net;
using System.Net.Http.Headers;
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
        private readonly List<(long? From, long? To)> _ranges = [];

        public List<long?> RangeStarts => Ranges.Select(r => r.From).ToList();

        public List<(long? From, long? To)> Ranges
        {
            get { lock (_ranges) return [.. _ranges]; }
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var range = request.Headers.Range?.Ranges.First();
            lock (_ranges) _ranges.Add((range?.From, range?.To));
            return Task.FromResult(respond(request));
        }
    }

    /// <summary>Answers like a server that honours ranges. The body can be cut short or stall.</summary>
    private static HttpResponseMessage Serve(byte[] file, HttpRequestMessage request, int? sendOnly = null, Action? stall = null)
    {
        var range = request.Headers.Range?.Ranges.First();
        long from = range?.From ?? 0;
        long to = range?.To ?? file.Length - 1;
        var slice = file[(int)from..(int)(to + 1)];
        var sent = slice[..Math.Min(sendOnly ?? slice.Length, slice.Length)];
        var response = new HttpResponseMessage(range is null ? HttpStatusCode.OK : HttpStatusCode.PartialContent)
        {
            Content = stall is null ? new ByteArrayContent(sent) : new StreamContent(new StallingStream(sent, stall)),
        };
        response.Content.Headers.ContentLength = slice.Length;
        if (range is not null) response.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, to, file.Length);
        return response;
    }

    /// <summary>Sends its bytes, then calls back and waits until the read is cancelled.</summary>
    private sealed class StallingStream(byte[] bytes, Action onStall) : Stream
    {
        private int _position;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_position < bytes.Length)
            {
                int count = Math.Min(buffer.Length, bytes.Length - _position);
                bytes.AsMemory(_position, count).CopyTo(buffer);
                _position += count;
                return count;
            }
            onStall();
            await Task.Delay(Timeout.Infinite, ct);
            return 0;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static HttpResponseMessage Body(HttpStatusCode status, byte[] bytes, long? declaredLength = null)
    {
        var content = new ByteArrayContent(bytes);
        if (declaredLength is { } length) content.Headers.ContentLength = length;
        return new HttpResponseMessage(status) { Content = content };
    }

    private Task Download(Handler handler) => Download(handler, _model);

    private static Task Download(Handler handler, CotabbyModel model, CancellationToken ct = default) =>
        new ModelDownloader(new HttpClient(handler), NullLogger<ModelDownloader>.Instance) { RetryDelay = TimeSpan.Zero }
            .DownloadAsync(model, progress: null, ct);

    /// <summary>Leaves a segmented download's plan for <see cref="Large"/> and the given part files behind.</summary>
    private void WriteSegments(params (int Index, byte[] Bytes)[] parts)
    {
        Directory.CreateDirectory(SegmentsPath);
        File.WriteAllText(Path.Combine(SegmentsPath, "plan.json"),
            """{"Total":64,"Segments":[{"Start":0,"End":15},{"Start":16,"End":31},{"Start":32,"End":47},{"Start":48,"End":63}]}""");
        foreach (var (index, bytes) in parts) File.WriteAllBytes(Path.Combine(SegmentsPath, index.ToString()), bytes);
    }

    // A 64 byte model fetched over four connections of 16 bytes each.
    private static readonly byte[] Large = [.. "GGUF"u8, .. Enumerable.Range(0, 60).Select(i => (byte)i)];

    private CotabbyModel LargeModel => _model with { ApproxSizeBytes = Large.Length };

    private static Task DownloadSegmented(Handler handler, CotabbyModel model, CancellationToken ct = default) =>
        new ModelDownloader(new HttpClient(handler), NullLogger<ModelDownloader>.Instance)
        {
            Connections = 4,
            SegmentedMinimumBytes = 1,
            RetryDelay = TimeSpan.Zero,
        }.DownloadAsync(model, progress: null, ct);

    private string SegmentsPath => FinalPath + ".segments";

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

    [Fact]
    public async Task Pause_KeepsThePartialAndReportsItAsPaused()
    {
        using var cts = new CancellationTokenSource();
        var handler = new Handler(request => Serve(Gguf, request, sendOnly: 6, stall: cts.Cancel));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Download(handler, _model, cts.Token));

        Assert.Equal(6, new FileInfo(FinalPath + ".part").Length);
        Assert.Equal(new ModelDownloadState.Paused(0.5), ModelDownloader.StateOnDisk(_model));
        Assert.Equal("Paused (50%)", ModelDownloader.StateOnDisk(_model).StatusText);
    }

    [Fact]
    public async Task Download_RefusesASecondDownloadOfTheSameModel()
    {
        using var cts = new CancellationTokenSource();
        var stalled = new TaskCompletionSource();
        var first = Download(new Handler(request => Serve(Gguf, request, sendOnly: 6, stall: stalled.SetResult)), _model, cts.Token);
        await stalled.Task;

        await Assert.ThrowsAsync<InvalidOperationException>(() => Download(new Handler(request => Serve(Gguf, request))));

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        await Download(new Handler(request => Serve(Gguf, request)));
        Assert.True(ModelDownloader.IsCached(_model));
    }

    [Fact]
    public void Cancel_DeletesThePartial()
    {
        WritePartial(Gguf[..5]);

        ModelDownloader.DiscardPartial(_model);

        Assert.False(File.Exists(FinalPath + ".part"));
        Assert.Equal(new ModelDownloadState.Idle(), ModelDownloader.StateOnDisk(_model));
        // Discarding when nothing is kept is harmless.
        ModelDownloader.DiscardPartial(_model);
    }

    [Fact]
    public async Task Download_RejectsAFileOfTheWrongSize()
    {
        var model = _model with { ApproxSizeBytes = Gguf.Length + 8 };

        var error = await Assert.ThrowsAsync<InvalidDataException>(
            () => Download(new Handler(_ => Body(HttpStatusCode.OK, Gguf)), model));

        Assert.Contains("expected 20", error.Message);
        Assert.False(ModelDownloader.IsCached(model));
        Assert.False(File.Exists(FinalPath + ".part"));
    }

    [Fact]
    public async Task Download_RejectsAPartialLongerThanTheCatalogSize()
    {
        WritePartial(Gguf);
        var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable));
        var model = _model with { ApproxSizeBytes = Gguf.Length - 2 };

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => Download(handler, model));

        Assert.Contains("may have been truncated", error.Message);
        Assert.False(ModelDownloader.IsCached(model));
        Assert.False(File.Exists(FinalPath + ".part"));
    }

    [Fact]
    public async Task Download_RetriesOnceAfterADroppedConnection()
    {
        int calls = 0;
        var handler = new Handler(_ => Interlocked.Increment(ref calls) == 1
            ? throw new HttpRequestException("connection reset")
            : Body(HttpStatusCode.OK, Gguf));

        await Download(handler);

        Assert.Equal(2, calls);
        Assert.True(ModelDownloader.IsCached(_model));
    }

    [Fact]
    public async Task Download_ResumesATruncatedTransferOnTheRetry()
    {
        int calls = 0;
        var handler = new Handler(request => Interlocked.Increment(ref calls) == 1
            ? Body(HttpStatusCode.OK, Gguf[..6], declaredLength: Gguf.Length)
            : Serve(Gguf, request));

        await Download(handler);

        Assert.Equal([null, 6L], handler.RangeStarts);
        Assert.Equal(Gguf, File.ReadAllBytes(FinalPath));
    }

    [Fact]
    public async Task Download_GivesUpAfterOneRetry()
    {
        var handler = new Handler(_ => throw new HttpRequestException("connection reset"));

        await Assert.ThrowsAsync<HttpRequestException>(() => Download(handler));

        Assert.Equal(2, handler.RangeStarts.Count);
    }

    [Fact]
    public async Task Download_DoesNotRetryAMissingFile()
    {
        var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        await Assert.ThrowsAsync<HttpRequestException>(() => Download(handler));

        Assert.Single(handler.RangeStarts);
    }

    [Fact]
    public async Task Segmented_FetchesEveryRangeAndJoinsThem()
    {
        var handler = new Handler(request => Serve(Large, request));

        await DownloadSegmented(handler, LargeModel);

        Assert.Equal(Large, File.ReadAllBytes(FinalPath));
        Assert.Equal(
            [(0L, 15L), (16L, 31L), (32L, 47L), (48L, 63L)],
            handler.Ranges.Select(r => (r.From!.Value, r.To!.Value)).Order().ToList());
        Assert.False(Directory.Exists(SegmentsPath));
        Assert.False(File.Exists(FinalPath + ".part"));
    }

    [Fact]
    public async Task Segmented_FallsBackToOneStreamWhenTheServerIgnoresRanges()
    {
        var handler = new Handler(_ => Body(HttpStatusCode.OK, Large));

        await DownloadSegmented(handler, LargeModel);

        Assert.Equal(Large, File.ReadAllBytes(FinalPath));
        Assert.Equal([(0L, 15L), (null, null)], handler.Ranges);
    }

    [Fact]
    public async Task Segmented_PauseKeepsEachPartAndResumeFetchesOnlyTheRest()
    {
        using var cts = new CancellationTokenSource();
        var paused = new Handler(request => Serve(Large, request, sendOnly: 8, stall: cts.Cancel));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DownloadSegmented(paused, LargeModel, cts.Token));

        Assert.True(Directory.Exists(SegmentsPath));
        var state = Assert.IsType<ModelDownloadState.Paused>(ModelDownloader.StateOnDisk(LargeModel));
        Assert.InRange(state.Progress!.Value, 0.1, 0.9);

        var resumed = new Handler(request => Serve(Large, request));
        await DownloadSegmented(resumed, LargeModel);

        Assert.Equal(Large, File.ReadAllBytes(FinalPath));
        // A part that kept its first 8 bytes asks only for the rest of its range.
        Assert.Contains(resumed.Ranges, r => r.From % 16 == 8);
        Assert.DoesNotContain(resumed.Ranges, r => r.From is null);
        Assert.False(Directory.Exists(SegmentsPath));
    }

    [Fact]
    public async Task Segmented_CancelDeletesEveryPart()
    {
        using var cts = new CancellationTokenSource();
        var handler = new Handler(request => Serve(Large, request, sendOnly: 8, stall: cts.Cancel));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DownloadSegmented(handler, LargeModel, cts.Token));

        ModelDownloader.DiscardPartial(LargeModel);

        Assert.False(Directory.Exists(SegmentsPath));
        Assert.Equal(new ModelDownloadState.Idle(), ModelDownloader.StateOnDisk(LargeModel));
    }

    [Fact]
    public async Task Segmented_RetriesAPartThatWasCutShort()
    {
        int calls = 0;
        var handler = new Handler(request =>
            Serve(Large, request, sendOnly: Interlocked.Increment(ref calls) == 2 ? 5 : null));

        await DownloadSegmented(handler, LargeModel);

        Assert.Equal(Large, File.ReadAllBytes(FinalPath));
        Assert.Contains(handler.Ranges, r => r.From % 16 == 5);
    }

    [Fact]
    public async Task Segmented_ResumesAnInterruptedJoinWithoutRefetchingJoinedParts()
    {
        // The join had appended parts 1 and half of 2 to part 0, and deleted part 1, when it stopped.
        WriteSegments((0, Large[..40]), (2, Large[32..48]));
        Assert.Equal(new ModelDownloadState.Paused(0.75), ModelDownloader.StateOnDisk(LargeModel));
        var handler = new Handler(request => Serve(Large, request));

        await DownloadSegmented(handler, LargeModel);

        Assert.Equal(Large, File.ReadAllBytes(FinalPath));
        Assert.Equal([(48L, 63L)], handler.Ranges);
    }

    [Fact]
    public async Task Segmented_StartsOverWhenAResumingServerIgnoresRanges()
    {
        WriteSegments((0, Large[..8]), (1, Large[16..20]));
        var handler = new Handler(_ => Body(HttpStatusCode.OK, Large));

        await DownloadSegmented(handler, LargeModel);

        Assert.Equal(Large, File.ReadAllBytes(FinalPath));
        Assert.False(Directory.Exists(SegmentsPath));
        Assert.Contains(handler.Ranges, r => r.From is null);
    }

    [Fact]
    public async Task Download_RetriesAStalledTransferWhereItStopped()
    {
        int calls = 0;
        var handler = new Handler(request => Interlocked.Increment(ref calls) == 1
            ? Serve(Gguf, request, sendOnly: 6, stall: () => { })
            : Serve(Gguf, request));
        var downloader = new ModelDownloader(new HttpClient(handler), NullLogger<ModelDownloader>.Instance)
        {
            RetryDelay = TimeSpan.Zero,
            StallTimeout = TimeSpan.FromMilliseconds(100),
        };

        await downloader.DownloadAsync(_model, progress: null, CancellationToken.None);

        Assert.Equal([null, 6L], handler.RangeStarts);
        Assert.Equal(Gguf, File.ReadAllBytes(FinalPath));
    }

    [Fact]
    public async Task Download_RefusesAResumeAtTheWrongOffset()
    {
        WritePartial(Gguf[..5]);
        var handler = new Handler(_ =>
        {
            var response = Body(HttpStatusCode.PartialContent, Gguf);
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(0, Gguf.Length - 1, Gguf.Length);
            return response;
        });

        await Assert.ThrowsAsync<IOException>(() => Download(handler));

        Assert.False(ModelDownloader.IsCached(_model));
        Assert.Equal(Gguf[..5], File.ReadAllBytes(FinalPath + ".part"));
    }

    [Fact]
    public async Task Segmented_RejectsAServerOfferingADifferentSize()
    {
        // The catalog expects 64 bytes but the server has 72, so the first range does not line up.
        byte[] other = [.. Large, .. new byte[8]];
        var handler = new Handler(request => Serve(other, request));

        await Assert.ThrowsAsync<InvalidDataException>(() => DownloadSegmented(handler, LargeModel));

        Assert.False(ModelDownloader.IsCached(LargeModel));
    }
}

public sealed class DownloadProgressTests
{
    private const long MB = 1 << 20;

    [Fact]
    public void Tracker_ReportsASmoothedSpeedAndEta()
    {
        var now = TimeSpan.Zero;
        var reports = new List<ModelDownloader.Progress>();
        var tracker = new DownloadProgressTracker(0, 100 * MB, new SyncProgress(reports.Add), () => now);

        now = TimeSpan.FromMilliseconds(100);
        tracker.Add(MB);
        Assert.Empty(reports);

        now = TimeSpan.FromSeconds(1);
        tracker.Add(MB);
        Assert.Equal(2.0 * MB, reports[^1].BytesPerSecond);

        now = TimeSpan.FromSeconds(1.5);
        tracker.Add(4 * MB);
        // The rate jumped to 8 MB/s. The average moves 30% of the way there.
        Assert.Equal(3.8 * MB, reports[^1].BytesPerSecond!.Value, precision: 3);
        Assert.Equal(6 * MB, reports[^1].BytesDownloaded);
        Assert.Equal(94 / 3.8, reports[^1].Eta!.Value.TotalSeconds, precision: 3);
    }

    [Fact]
    public void Tracker_HasNoEtaWithoutATotal()
    {
        var now = TimeSpan.Zero;
        var reports = new List<ModelDownloader.Progress>();
        var tracker = new DownloadProgressTracker(0, null, new SyncProgress(reports.Add), () => now);

        now = TimeSpan.FromSeconds(1);
        tracker.Add(MB);

        Assert.Equal(MB, reports[^1].BytesPerSecond);
        Assert.Null(reports[^1].Eta);
    }

    [Theory]
    [InlineData(512, "512 B/s")]
    [InlineData(300 * 1024, "300 KB/s")]
    [InlineData(12.3 * 1024 * 1024, "12.3 MB/s")]
    [InlineData(1.5 * 1024 * 1024 * 1024, "1.5 GB/s")]
    public void FormatSpeed_MatchesUpstream(double bytesPerSecond, string expected) =>
        Assert.Equal(expected, ModelDownloadState.FormatSpeed(bytesPerSecond));

    [Theory]
    [InlineData(45, "45s")]
    [InlineData(75, "1m 15s")]
    [InlineData(3725, "1h 2m 5s")]
    [InlineData(3600, "1h")]
    [InlineData(0, "0s")]
    public void FormatEta_MatchesUpstream(int seconds, string expected) =>
        Assert.Equal(expected, ModelDownloadState.FormatEta(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void StatusText_MatchesUpstream()
    {
        Assert.Equal("Downloading 42% (12.3 MB/s · ETA 1m 15s)",
            new ModelDownloadState.Downloading(0.42, "12.3 MB/s", "1m 15s").StatusText);
        Assert.Equal("Downloading 42% (12.3 MB/s)", new ModelDownloadState.Downloading(0.42, "12.3 MB/s").StatusText);
        Assert.Equal("Downloading 42%", new ModelDownloadState.Downloading(0.42).StatusText);
        Assert.Equal("Downloading", new ModelDownloadState.Downloading(null).StatusText);
        Assert.Equal("Paused (7%)", new ModelDownloadState.Paused(0.07).StatusText);
        Assert.Equal("Not installed", new ModelDownloadState.Idle().StatusText);
        Assert.Equal("Installed", new ModelDownloadState.Downloaded().StatusText);
    }

    [Fact]
    public void From_FormatsAProgressReport()
    {
        var state = ModelDownloadState.From(
            new ModelDownloader.Progress(42 * MB, 100 * MB, 12.3 * MB, TimeSpan.FromSeconds(75)));

        Assert.Equal("Downloading 42% (12.3 MB/s · ETA 1m 15s)", state.StatusText);
        Assert.Equal(0.42, state.ProgressFraction);
    }

    private sealed class SyncProgress(Action<ModelDownloader.Progress> report) : IProgress<ModelDownloader.Progress>
    {
        public void Report(ModelDownloader.Progress value) => report(value);
    }
}
