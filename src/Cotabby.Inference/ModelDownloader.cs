using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Cotabby.Core.Models;
using Microsoft.Extensions.Logging;

namespace Cotabby.Inference;

/// <summary>
/// Downloads a model file into the local cache. A large file is fetched over several
/// connections at once, each into its own part file. A server that refuses ranges gets a
/// single stream into one .part file. Either kind of partial is kept when the download stops
/// and is resumed with HTTP range requests on the next attempt.
/// </summary>
public sealed class ModelDownloader
{
    private static readonly byte[] GgufMagic = "GGUF"u8.ToArray();

    private readonly HttpClient _http;
    private readonly ILogger<ModelDownloader> _logger;

    public ModelDownloader(HttpClient http, ILogger<ModelDownloader> logger)
    {
        _http = http;
        _logger = logger;
    }

    public sealed record Progress(long BytesDownloaded, long? TotalBytes, double? BytesPerSecond = null, TimeSpan? Eta = null);

    /// <summary>Parallel connections for a large file. Upstream runs aria2c with eight.</summary>
    public int Connections { get; init; } = 8;

    /// <summary>Files smaller than this use one stream, where the extra requests would not pay off.</summary>
    public long SegmentedMinimumBytes { get; init; } = 64L << 20;

    /// <summary>Wait before the one automatic retry after a dropped connection.</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(2);

    public static string LocalPath(CotabbyModel model) =>
        Path.Combine(ModelCatalog.DefaultLocalDirectory(), model.FileName);

    private static string PartPath(CotabbyModel model) => LocalPath(model) + ".part";

    private static string SegmentsPath(CotabbyModel model) => LocalPath(model) + ".segments";

    /// <summary>
    /// True when the model file is in place. A download only reaches its final
    /// name once it is complete, so presence plus the GGUF signature is enough.
    /// </summary>
    public static bool IsCached(CotabbyModel model) => IsGguf(LocalPath(model));

    /// <summary>Bytes kept from a stopped download, or 0 when there is none.</summary>
    public static long PartialBytes(CotabbyModel model)
    {
        try
        {
            if (ReadPlan(SegmentsPath(model), Expected(model)) is { } plan)
            {
                return plan.Segments.Select((s, i) => Math.Min(FileLength(SegmentFile(model, i)), s.Size)).Sum();
            }
            return FileLength(PartPath(model));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>What the disk says about the model. A kept partial reads as paused.</summary>
    public static ModelDownloadState StateOnDisk(CotabbyModel model)
    {
        if (IsCached(model)) return new ModelDownloadState.Downloaded();
        long kept = PartialBytes(model);
        if (kept <= 0) return new ModelDownloadState.Idle();
        return new ModelDownloadState.Paused(Expected(model) is { } total ? Math.Min(1, (double)kept / total) : null);
    }

    /// <summary>Deletes a stopped download's partial files. Call it only once the download has stopped.</summary>
    public static void DiscardPartial(CotabbyModel model)
    {
        File.Delete(PartPath(model));
        if (Directory.Exists(SegmentsPath(model))) Directory.Delete(SegmentsPath(model), recursive: true);
    }

    /// <summary>
    /// Downloads the model unless it is cached. A cancelled download keeps its partial files,
    /// so cancelling is a pause. One dropped connection is retried, resuming where it stopped.
    /// </summary>
    public async Task DownloadAsync(CotabbyModel model, IProgress<Progress>? progress, CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                await DownloadOnceAsync(model, progress, ct).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (attempt == 0 && !ct.IsCancellationRequested && IsTransient(ex))
            {
                _logger.LogWarning(ex, "Download of {Model} dropped. Retrying once.", model.Id);
                await Task.Delay(RetryDelay, ct).ConfigureAwait(false);
            }
        }
    }

    private static bool IsTransient(Exception ex) => ex switch
    {
        HttpRequestException http => http.StatusCode is null || (int)http.StatusCode >= 500,
        TaskCanceledException { InnerException: TimeoutException } => true,
        IOException => true,
        _ => false,
    };

    private async Task DownloadOnceAsync(CotabbyModel model, IProgress<Progress>? progress, CancellationToken ct)
    {
        var finalPath = LocalPath(model);
        if (IsGguf(finalPath)) return;
        if (model.IsCustom) throw new FileNotFoundException("The model file is no longer in the models folder.", finalPath);

        Directory.CreateDirectory(ModelCatalog.DefaultLocalDirectory());
        var partPath = PartPath(model);
        var segmentsPath = SegmentsPath(model);
        long? expected = Expected(model);

        if (expected is { } size && FileLength(partPath) == size)
        {
            // A joined download whose last step was interrupted only needs checking.
            if (Directory.Exists(segmentsPath)) Directory.Delete(segmentsPath, recursive: true);
        }
        else if (ReadPlan(segmentsPath, expected) is { } plan)
        {
            await DownloadSegmentsAsync(model, plan, first: null, progress, ct).ConfigureAwait(false);
        }
        else
        {
            if (Directory.Exists(segmentsPath)) Directory.Delete(segmentsPath, recursive: true);
            bool segmented = !File.Exists(partPath) && Connections > 1 && expected >= SegmentedMinimumBytes
                && await TryStartSegmentsAsync(model, expected!.Value, progress, ct).ConfigureAwait(false);
            if (!segmented) await DownloadSingleAsync(model, expected, progress, ct).ConfigureAwait(false);
        }

        Promote(partPath, finalPath, expected);
        _logger.LogInformation("Model {Model} downloaded to {Path}.", model.Id, finalPath);
    }

    /// <summary>The catalog size. Built-in sizes are the exact Content-Length of the file.</summary>
    private static long? Expected(CotabbyModel model) => model.ApproxSizeBytes > 0 ? model.ApproxSizeBytes : null;

    private static void Promote(string partPath, string finalPath, long? expected)
    {
        long actual = FileLength(partPath);
        if (expected is { } size && actual != size)
        {
            var message = $"Downloaded file is {actual} bytes; expected {size}. The download may have been truncated.";
            // A short file can still be resumed. A long one cannot be the right file.
            if (actual < size) throw new IOException(message);
            File.Delete(partPath);
            throw new InvalidDataException(message);
        }
        if (!IsGguf(partPath))
        {
            File.Delete(partPath);
            throw new InvalidDataException("The downloaded file is not a GGUF model.");
        }
        File.Move(partPath, finalPath, overwrite: true);
    }

    // ---------- Single stream ----------

    private async Task DownloadSingleAsync(CotabbyModel model, long? expected, IProgress<Progress>? progress, CancellationToken ct)
    {
        var partPath = PartPath(model);
        long resumeFrom = FileLength(partPath);

        using var response = await GetAsync(model, resumeFrom > 0 ? resumeFrom : null, null, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable) return;

        response.EnsureSuccessStatusCode();
        // A server that ignores the range sends the whole file again, so the partial is discarded.
        bool resumed = response.StatusCode == HttpStatusCode.PartialContent;
        if (!resumed) resumeFrom = 0;
        long? total = response.Content.Headers.ContentLength + resumeFrom;
        if (total is { } offered && expected is { } size && offered != size)
        {
            File.Delete(partPath);
            throw new InvalidDataException(
                $"The server is offering {offered} bytes; expected {size}. The model file may have changed.");
        }

        var tracker = new DownloadProgressTracker(resumeFrom, total ?? expected, progress);
        await using (var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var file = OpenForAppend(partPath, append: resumed))
        {
            await CopyAsync(source, file, tracker, ct).ConfigureAwait(false);
        }
        tracker.Complete();

        if (total is { } declared && tracker.Bytes != declared)
        {
            throw new IOException(
                $"The download stopped at {tracker.Bytes} of {declared} bytes. Try again to resume it.");
        }
    }

    // ---------- Segments ----------

    private sealed record Segment(long Start, long End)
    {
        public long Size => End - Start + 1;
    }

    private sealed record SegmentPlan(long Total, List<Segment> Segments);

    private static string SegmentFile(CotabbyModel model, int index) => Path.Combine(SegmentsPath(model), index.ToString());

    private static string PlanFile(string segmentsPath) => Path.Combine(segmentsPath, "plan.json");

    private static SegmentPlan Plan(long total, int connections)
    {
        long size = (total + connections - 1) / connections;
        var segments = new List<Segment>();
        for (long start = 0; start < total; start += size) segments.Add(new Segment(start, Math.Min(start + size, total) - 1));
        return new SegmentPlan(total, segments);
    }

    /// <summary>Reads a saved plan. One that does not tile the expected file is treated as absent.</summary>
    private static SegmentPlan? ReadPlan(string segmentsPath, long? expected)
    {
        try
        {
            var plan = JsonSerializer.Deserialize<SegmentPlan>(File.ReadAllText(PlanFile(segmentsPath)));
            if (plan?.Segments is not { Count: > 0 } segments || plan.Total != expected) return null;
            long next = 0;
            foreach (var segment in segments)
            {
                if (segment.Start != next || segment.End < segment.Start) return null;
                next = segment.End + 1;
            }
            return next == plan.Total ? plan : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Asks for the first segment. When the server answers with that exact range the plan is saved
    /// and every segment downloads in parallel. Otherwise the caller falls back to one stream.
    /// </summary>
    private async Task<bool> TryStartSegmentsAsync(CotabbyModel model, long total, IProgress<Progress>? progress, CancellationToken ct)
    {
        var plan = Plan(total, Connections);
        var first = await GetAsync(model, 0, plan.Segments[0].End, ct).ConfigureAwait(false);
        if (!IsRange(first, 0, plan.Segments[0].End, total))
        {
            first.Dispose();
            return false;
        }

        try
        {
            Directory.CreateDirectory(SegmentsPath(model));
            File.WriteAllText(PlanFile(SegmentsPath(model)), JsonSerializer.Serialize(plan));
        }
        catch
        {
            first.Dispose();
            throw;
        }
        await DownloadSegmentsAsync(model, plan, first, progress, ct).ConfigureAwait(false);
        return true;
    }

    private async Task DownloadSegmentsAsync(
        CotabbyModel model, SegmentPlan plan, HttpResponseMessage? first, IProgress<Progress>? progress, CancellationToken ct)
    {
        long kept = 0;
        for (int i = 0; i < plan.Segments.Count; i++)
        {
            var path = SegmentFile(model, i);
            // A segment longer than its range was part way through joining. Trim it back.
            if (FileLength(path) > plan.Segments[i].Size)
            {
                using var file = new FileStream(path, FileMode.Open, FileAccess.Write);
                file.SetLength(plan.Segments[i].Size);
            }
            kept += Math.Min(FileLength(path), plan.Segments[i].Size);
        }

        var tracker = new DownloadProgressTracker(kept, plan.Total, progress);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Exception? failure = null;
        var workers = plan.Segments.Select((_, i) => FetchSegmentAsync(i, i == 0 ? first : null)).ToList();
        try
        {
            await Task.WhenAll(workers).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The first real failure is reported, not the cancellations it caused in the other workers.
            ct.ThrowIfCancellationRequested();
            ExceptionDispatchInfo.Throw(failure ?? ex);
        }
        tracker.Complete();

        await JoinSegmentsAsync(model, plan, ct).ConfigureAwait(false);

        async Task FetchSegmentAsync(int index, HttpResponseMessage? response)
        {
            var segment = plan.Segments[index];
            var path = SegmentFile(model, index);
            try
            {
                long have = Math.Min(FileLength(path), segment.Size);
                if (have == segment.Size) return;
                long from = segment.Start + have;
                // The probe response covers the whole first segment, so it only fits a fresh one.
                if (response is null || have > 0)
                {
                    response?.Dispose();
                    response = await GetAsync(model, from, segment.End, stop.Token).ConfigureAwait(false);
                }
                if (!IsRange(response, from, segment.End, plan.Total))
                {
                    response.EnsureSuccessStatusCode();
                    throw new IOException("The server stopped answering range requests.");
                }

                long copied;
                await using (var source = await response.Content.ReadAsStreamAsync(stop.Token).ConfigureAwait(false))
                await using (var file = OpenForAppend(path, append: true))
                {
                    copied = await CopyAsync(source, file, tracker, stop.Token).ConfigureAwait(false);
                }
                if (have + copied != segment.Size)
                {
                    throw new IOException(
                        $"Part {index + 1} stopped at {have + copied} of {segment.Size} bytes. Try again to resume it.");
                }
            }
            catch (Exception ex) when (!stop.IsCancellationRequested)
            {
                Interlocked.CompareExchange(ref failure, ex, null);
                stop.Cancel();
                throw;
            }
            finally
            {
                response?.Dispose();
            }
        }
    }

    private static async Task JoinSegmentsAsync(CotabbyModel model, SegmentPlan plan, CancellationToken ct)
    {
        var firstPath = SegmentFile(model, 0);
        await using (var target = OpenForAppend(firstPath, append: true))
        {
            for (int i = 1; i < plan.Segments.Count; i++)
            {
                await using (var source = File.OpenRead(SegmentFile(model, i)))
                {
                    await source.CopyToAsync(target, ct).ConfigureAwait(false);
                }
                await target.FlushAsync(ct).ConfigureAwait(false);
                // Deleted only once its bytes are in the first file, so an interruption loses nothing.
                File.Delete(SegmentFile(model, i));
            }
        }
        File.Move(firstPath, PartPath(model), overwrite: true);
        Directory.Delete(SegmentsPath(model), recursive: true);
    }

    private static bool IsRange(HttpResponseMessage response, long from, long to, long total) =>
        response.StatusCode == HttpStatusCode.PartialContent
        && response.Content.Headers.ContentRange is { From: var f, To: var t, Length: var length }
        && f == from && t == to && length == total;

    // ---------- Shared ----------

    private async Task<HttpResponseMessage> GetAsync(CotabbyModel model, long? from, long? to, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, model.DownloadUrl);
        if (from is not null) request.Headers.Range = new RangeHeaderValue(from, to);
        return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
    }

    private static FileStream OpenForAppend(string path, bool append) => new(
        path, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None,
        bufferSize: 1 << 16, useAsync: true);

    private static async Task<long> CopyAsync(Stream source, Stream target, DownloadProgressTracker tracker, CancellationToken ct)
    {
        var buffer = new byte[1 << 16];
        long copied = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            copied += read;
            tracker.Add(read);
        }
        return copied;
    }

    private static long FileLength(string path) => File.Exists(path) ? new FileInfo(path).Length : 0;

    private static bool IsGguf(string path)
    {
        try
        {
            using var file = File.OpenRead(path);
            Span<byte> header = stackalloc byte[4];
            return file.Read(header) == header.Length && header.SequenceEqual(GgufMagic);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

/// <summary>
/// Counts downloaded bytes from any number of connections and reports progress at most every
/// quarter second. Speed is an exponential moving average of the rate between reports.
/// </summary>
public sealed class DownloadProgressTracker
{
    private const double Smoothing = 0.3;
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(250);

    private readonly object _gate = new();
    private readonly IProgress<ModelDownloader.Progress>? _progress;
    private readonly Func<TimeSpan> _clock;
    private readonly long? _total;
    private long _bytes;
    private long _sampledBytes;
    private TimeSpan _sampledAt;
    private double? _speed;

    public DownloadProgressTracker(
        long startBytes, long? total, IProgress<ModelDownloader.Progress>? progress, Func<TimeSpan>? clock = null)
    {
        _bytes = _sampledBytes = startBytes;
        _total = total;
        _progress = progress;
        var watch = Stopwatch.StartNew();
        _clock = clock ?? (() => watch.Elapsed);
        _sampledAt = _clock();
    }

    public long Bytes
    {
        get { lock (_gate) return _bytes; }
    }

    public double? BytesPerSecond
    {
        get { lock (_gate) return _speed; }
    }

    public void Add(long count)
    {
        lock (_gate)
        {
            _bytes += count;
            var now = _clock();
            var elapsed = now - _sampledAt;
            if (elapsed < Interval) return;

            double rate = (_bytes - _sampledBytes) / elapsed.TotalSeconds;
            _speed = _speed is { } previous ? previous + Smoothing * (rate - previous) : rate;
            _sampledBytes = _bytes;
            _sampledAt = now;
            Report();
        }
    }

    /// <summary>Reports the final count.</summary>
    public void Complete()
    {
        lock (_gate) Report();
    }

    private void Report()
    {
        TimeSpan? eta = _total is { } total && _speed is > 0 and var speed
            ? TimeSpan.FromSeconds(Math.Max(0, total - _bytes) / speed)
            : null;
        _progress?.Report(new ModelDownloader.Progress(_bytes, _total, _speed, eta));
    }
}
