using System.IO;
using Microsoft.Extensions.Logging;

namespace Cotabby.App.Hosting;

/// <summary>
/// Append-only single-file logger, so diagnostics land at a known path without redirecting
/// stdout. There is no rotation and no batching. Every call writes and flushes under one lock,
/// which keeps lines from different threads whole.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private const long MaxBytes = 5 * 1024 * 1024;

    private readonly object _gate = new();
    private StreamWriter? _writer;

    public FileLoggerProvider(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        // The log is for diagnosing the current session, so it starts over once it has grown large.
        if (File.Exists(path) && new FileInfo(path).Length > MaxBytes) File.Delete(path);
        _writer = new StreamWriter(new FileStream(
            path, FileMode.Append, FileAccess.Write, FileShare.Read))
        { AutoFlush = true };
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(categoryName, this);

    private bool IsOpen
    {
        get
        {
            lock (_gate) return _writer is not null;
        }
    }

    /// <summary>Writes one line, or drops it once the provider is disposed, so late messages at shutdown never throw.</summary>
    private void Write(string line)
    {
        lock (_gate)
        {
            _writer?.WriteLine(line);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }

    private sealed class FileLogger(string category, FileLoggerProvider owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug && owner.IsOpen;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var line = $"{DateTime.Now:HH:mm:ss.fff} {ShortLevel(logLevel)}: {category}[{eventId.Id}] {formatter(state, exception)}";
            if (exception is not null) line += $" :: {exception}";
            owner.Write(line);
        }

        private static string ShortLevel(LogLevel l) => l switch
        {
            LogLevel.Trace => "trce",
            LogLevel.Debug => "dbug",
            LogLevel.Information => "info",
            LogLevel.Warning => "warn",
            LogLevel.Error => "fail",
            LogLevel.Critical => "crit",
            _ => "info",
        };
    }
}
