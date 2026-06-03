using System.Net.Http;
using Cotabby.Core.Focus;
using Cotabby.Core.Models;
using Cotabby.Core.Suggestions;
using Cotabby.Inference;
using Microsoft.Extensions.Logging;

// One-shot end-to-end verification of the inference path. Downloads the smallest
// Qwen2.5-Coder model if not cached, loads it, runs a single generation against
// a fixed prefix, and prints the streamed output. Intended for CI / manual
// "does the inference layer actually work" checks.

using var loggerFactory = LoggerFactory.Create(b =>
{
    b.SetMinimumLevel(LogLevel.Information);
    b.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; });
});

using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(60) };
http.DefaultRequestHeaders.UserAgent.ParseAdd("Cotabby-Smoke/0.1");

var model = ModelCatalog.All[0]; // 0.5B
var path = ModelDownloader.LocalPath(model);
Console.WriteLine($"Model:  {model.DisplayName}");
Console.WriteLine($"Path:   {path}");

if (!ModelDownloader.IsCached(model))
{
    Console.WriteLine("Downloading…");
    var downloader = new ModelDownloader(http, loggerFactory.CreateLogger<ModelDownloader>());
    var lastPct = -1;
    var progress = new Progress<ModelDownloader.Progress>(p =>
    {
        if (p.TotalBytes is { } total && total > 0)
        {
            int pct = (int)(100.0 * p.BytesDownloaded / total);
            if (pct != lastPct) { Console.Write($"\r  {pct,3}% {p.BytesDownloaded / 1024 / 1024,6:N0} / {total / 1024 / 1024,6:N0} MB"); lastPct = pct; }
        }
    });
    await downloader.DownloadAsync(model, progress, CancellationToken.None);
    Console.WriteLine();
}
else
{
    Console.WriteLine("Already cached.");
}

Console.WriteLine("Loading runtime…");
await using var runtime = new LlamaRuntimeManager(loggerFactory.CreateLogger<LlamaRuntimeManager>());
await runtime.LoadAsync(model, path, CancellationToken.None);

var engine = new LlamaSuggestionEngine(runtime, loggerFactory.CreateLogger<LlamaSuggestionEngine>());

var fixedText = args.Length > 0 ? args[0] : "def fibonacci(n):\n    if n < 2:\n        return n\n    return ";
var fakeField = new FocusedField
{
    ElementHandle = new object(),
    ProcessId = 0,
    ProcessName = "smoketest",
    Text = fixedText,
    CaretOffset = fixedText.Length,
    CaretRect = ScreenRect.Empty,
    FieldRect = ScreenRect.Empty,
    IsSingleLine = false,
    IsSecure = false,
};
var request = SuggestionRequestFactory.Build(fakeField, "smoke");

Console.WriteLine();
Console.WriteLine("Prompt prefix (tail):");
Console.WriteLine($"  {request.Prefix.Replace("\n", "\\n")}");
Console.WriteLine();
Console.WriteLine("Streamed completion:");
Console.Write("  ");

var sw = System.Diagnostics.Stopwatch.StartNew();
var chunks = 0;
var chars = 0;
await foreach (var chunk in engine.GenerateAsync(request, CancellationToken.None))
{
    if (chunk.IsFinal) break;
    Console.Write(chunk.Text);
    chunks++;
    chars += chunk.Text.Length;
}
sw.Stop();
Console.WriteLine();
Console.WriteLine();
Console.WriteLine($"Chunks: {chunks}, chars: {chars}, elapsed: {sw.ElapsedMilliseconds} ms");
if (sw.ElapsedMilliseconds > 0)
{
    Console.WriteLine($"Throughput: {chars * 1000.0 / sw.ElapsedMilliseconds:N1} chars/sec");
}
