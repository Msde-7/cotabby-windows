using System.Net;
using System.Net.Http.Headers;
using Cotabby.Core.Models;
using Microsoft.Extensions.Logging;

namespace Cotabby.Inference;

/// <summary>
/// Streams a model file into the local cache. A partial download is kept and
/// resumed with an HTTP range request on the next attempt.
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

    public sealed record Progress(long BytesDownloaded, long? TotalBytes);

    public static string LocalPath(CotabbyModel model) =>
        Path.Combine(ModelCatalog.DefaultLocalDirectory(), model.FileName);

    /// <summary>
    /// True when the model file is in place. A download only reaches its final
    /// name once it is complete, so presence plus the GGUF signature is enough.
    /// </summary>
    public static bool IsCached(CotabbyModel model) => IsGguf(LocalPath(model));

    public async Task DownloadAsync(CotabbyModel model, IProgress<Progress>? progress, CancellationToken ct)
    {
        var finalPath = LocalPath(model);
        if (IsGguf(finalPath)) return;
        if (model.IsCustom) throw new FileNotFoundException("The model file is no longer in the models folder.", finalPath);

        Directory.CreateDirectory(ModelCatalog.DefaultLocalDirectory());
        var partPath = finalPath + ".part";
        long resumeFrom = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;

        using var request = new HttpRequestMessage(HttpMethod.Get, model.DownloadUrl);
        if (resumeFrom > 0) request.Headers.Range = new RangeHeaderValue(resumeFrom, null);
        using var response = await _http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        if (response.StatusCode != HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            response.EnsureSuccessStatusCode();
            // A server that ignores the range sends the whole file again, so the partial is discarded.
            bool resumed = response.StatusCode == HttpStatusCode.PartialContent;
            if (!resumed) resumeFrom = 0;
            long? total = response.Content.Headers.ContentLength + resumeFrom;

            long downloaded = await CopyAsync(response, partPath, resumed, resumeFrom, total, progress, ct)
                .ConfigureAwait(false);
            if (total is { } expected && downloaded != expected)
            {
                throw new IOException(
                    $"The download stopped at {downloaded} of {expected} bytes. Try again to resume it.");
            }
        }

        if (!IsGguf(partPath))
        {
            File.Delete(partPath);
            throw new InvalidDataException("The downloaded file is not a GGUF model.");
        }
        File.Move(partPath, finalPath, overwrite: true);
        _logger.LogInformation("Model {Model} downloaded to {Path}.", model.Id, finalPath);
    }

    private static async Task<long> CopyAsync(
        HttpResponseMessage response, string partPath, bool append, long startAt, long? total,
        IProgress<Progress>? progress, CancellationToken ct)
    {
        await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var file = new FileStream(
            partPath, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None,
            bufferSize: 1 << 16, useAsync: true);

        var buffer = new byte[1 << 16];
        long downloaded = startAt;
        var lastReport = DateTime.MinValue;
        int read;
        while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            downloaded += read;
            if (DateTime.UtcNow - lastReport > TimeSpan.FromMilliseconds(250))
            {
                progress?.Report(new Progress(downloaded, total));
                lastReport = DateTime.UtcNow;
            }
        }
        progress?.Report(new Progress(downloaded, total));
        return downloaded;
    }

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
