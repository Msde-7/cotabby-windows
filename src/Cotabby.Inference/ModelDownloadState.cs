using System.Globalization;

namespace Cotabby.Inference;

/// <summary>
/// Where one model's download stands, as the settings rows and the first-run window show it.
/// Mirrors upstream's idle, downloading, paused, downloaded and failed states.
/// </summary>
public abstract record ModelDownloadState
{
    private ModelDownloadState() { }

    public sealed record Idle : ModelDownloadState;

    public sealed record Downloading(double? Progress, string? Speed = null, string? Eta = null) : ModelDownloadState;

    public sealed record Paused(double? Progress) : ModelDownloadState;

    public sealed record Downloaded : ModelDownloadState;

    public sealed record Failed(string Message) : ModelDownloadState;

    /// <summary>Builds the downloading state from a progress report.</summary>
    public static Downloading From(ModelDownloader.Progress progress) => new(
        progress.TotalBytes is > 0 and var total ? (double)progress.BytesDownloaded / total : null,
        progress.BytesPerSecond is > 0 and var speed ? FormatSpeed(speed) : null,
        progress.Eta is { } eta ? FormatEta(eta) : null);

    public string StatusText => this switch
    {
        Idle => "Not installed",
        Downloading { Progress: { } p, Speed: { } speed, Eta: { } eta } =>
            $"Downloading {Percent(p)}% ({speed} · ETA {eta})",
        Downloading { Progress: { } p, Speed: { } speed } => $"Downloading {Percent(p)}% ({speed})",
        Downloading { Progress: { } p } => $"Downloading {Percent(p)}%",
        Downloading => "Downloading",
        Paused { Progress: { } p } => $"Paused ({Percent(p)}%)",
        Paused => "Paused",
        Downloaded => "Installed",
        Failed failed => failed.Message,
        _ => "",
    };

    public double? ProgressFraction => this switch
    {
        Downloading { Progress: { } p } => Math.Clamp(p, 0, 1),
        Paused { Progress: { } p } => Math.Clamp(p, 0, 1),
        _ => null,
    };

    public static int Percent(double fraction) => (int)Math.Round(Math.Clamp(fraction, 0, 1) * 100);

    /// <summary>Formats like aria2's speed column as upstream shows it, for example "12.3 MB/s".</summary>
    public static string FormatSpeed(double bytesPerSecond)
    {
        var c = CultureInfo.InvariantCulture;
        if (bytesPerSecond >= 1 << 30) return (bytesPerSecond / (1 << 30)).ToString("0.0", c) + " GB/s";
        if (bytesPerSecond >= 1 << 20) return (bytesPerSecond / (1 << 20)).ToString("0.0", c) + " MB/s";
        if (bytesPerSecond >= 1 << 10) return (bytesPerSecond / (1 << 10)).ToString("0", c) + " KB/s";
        return ((long)bytesPerSecond).ToString(c) + " B/s";
    }

    /// <summary>Formats like aria2's ETA column as upstream shows it, for example "1h 2m 5s" or "1m 15s".</summary>
    public static string FormatEta(TimeSpan eta)
    {
        long seconds = Math.Max(0, (long)Math.Ceiling(eta.TotalSeconds));
        long hours = seconds / 3600, minutes = seconds / 60 % 60, rest = seconds % 60;
        var parts = new List<string>();
        if (hours > 0) parts.Add($"{hours}h");
        if (minutes > 0) parts.Add($"{minutes}m");
        if (rest > 0 || parts.Count == 0) parts.Add($"{rest}s");
        return string.Join(' ', parts);
    }
}
