using System.Net.Http.Headers;
using Cotabby.Core.Models;
using Microsoft.Extensions.Logging;

namespace Cotabby.Inference;

/// <summary>
/// Streams a model file from its Hugging Face URL into the local cache, with
/// HTTP <c>Range</c> resume support so a half-finished download survives a
/// crash or a tray-quit.
/// </summary>
public sealed class ModelDownloader
{
    private readonly HttpClient _http;
    private readonly ILogger<ModelDownloader> _logger;

    public ModelDownloader(HttpClient http, ILogger<ModelDownloader> logger)
    {
        _http = http;
        _logger = logger;
    }

    public sealed record Progress(long BytesDownloaded, long? TotalBytes);

    /// <summary>
    /// Resolve the local path that <paramref name="model"/> would download to,
    /// regardless of whether the file currently exists.
    /// </summary>
    public static string LocalPath(CotabbyModel model) =>
        Path.Combine(ModelCatalog.DefaultLocalDirectory(), model.FileName);

    /// <summary>
    /// True if the cached file is at least as large as the model's stated
    /// approximate size minus a tolerance — a quick "is the download finished"
    /// check that avoids hashing the whole file on startup.
    /// </summary>
    public static bool IsCached(CotabbyModel model)
    {
        var path = LocalPath(model);
        if (!File.Exists(path)) return false;
        var len = new FileInfo(path).Length;
        // Tolerance: HF compressed sizes can be ~5% off the in-memory estimate.
        return len >= model.ApproxSizeBytes * 0.95;
    }

    public async Task DownloadAsync(
        CotabbyModel model,
        IProgress<Progress>? progress,
        CancellationToken ct)
    {
        var dir = ModelCatalog.DefaultLocalDirectory();
        Directory.CreateDirectory(dir);
        var finalPath = LocalPath(model);
        var partPath = finalPath + ".part";

        long resumeFrom = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;
        if (File.Exists(finalPath))
        {
            _logger.LogInformation("Model {Model} already present at {Path}.", model.Id, finalPath);
            return;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, model.DownloadUrl);
        if (resumeFrom > 0)
        {
            request.Headers.Range = new RangeHeaderValue(resumeFrom, null);
            _logger.LogInformation("Resuming download from byte {Offset}.", resumeFrom);
        }

        using var response = await _http.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        // 416 (Range Not Satisfiable) happens if the existing .part is already
        // the whole file — promote it and exit.
        if ((int)response.StatusCode == 416)
        {
            File.Move(partPath, finalPath, overwrite: true);
            return;
        }
        if (!response.IsSuccessStatusCode)
        {
            string body = "";
            try { body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false); } catch { }
            _logger.LogError(
                "Download failed: status={Status} url={Url} finalUrl={Final} body={Body}",
                response.StatusCode, model.DownloadUrl,
                response.RequestMessage?.RequestUri, body[..Math.Min(body.Length, 200)]);
            response.EnsureSuccessStatusCode();
        }

        long? total = response.Content.Headers.ContentLength;
        if (total.HasValue && response.StatusCode == System.Net.HttpStatusCode.PartialContent)
        {
            total += resumeFrom;
        }

        await using var net = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var file = new FileStream(
            partPath, FileMode.Append, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);

        var buffer = new byte[1 << 16];
        long downloaded = resumeFrom;
        var lastReport = DateTime.UtcNow;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            int read = await net.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
            if (read == 0) break;
            await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            downloaded += read;

            var now = DateTime.UtcNow;
            if ((now - lastReport) > TimeSpan.FromMilliseconds(250))
            {
                progress?.Report(new Progress(downloaded, total));
                lastReport = now;
            }
        }
        progress?.Report(new Progress(downloaded, total));

        await file.DisposeAsync().ConfigureAwait(false);
        File.Move(partPath, finalPath, overwrite: true);
        _logger.LogInformation("Model {Model} downloaded to {Path}.", model.Id, finalPath);
    }
}
