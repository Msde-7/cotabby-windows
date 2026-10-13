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
    private readonly string _path;
    private readonly object _gate = new();
    private StreamWriter? _writer;

    public FileLoggerProvider(string path)
    {
        _path = path;
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        _writer = new StreamWriter(new FileStream(
            path, FileMode.Append, FileAccess.Write, FileShare.Read))
        { AutoFlush = true };
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(categoryName, _gate, _writer);

    public void Dispose()
    {
        lock (_gate)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }

    private sealed class FileLogger : ILogger
    {
        private readonly string _category;
        private readonly object _gate;
        private readonly StreamWriter? _writer;

        public FileLogger(string category, object gate, StreamWriter? writer)
        {
            _category = category;
            _gate = gate;
            _writer = writer;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => _writer is not null && logLevel >= LogLevel.Debug;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel) || _writer is null) return;
            var line = $"{DateTime.Now:HH:mm:ss.fff} {ShortLevel(logLevel)}: {_category}[{eventId.Id}] {formatter(state, exception)}";
            if (exception is not null) line += $" :: {exception}";
            lock (_gate)
            {
                _writer.WriteLine(line);
            }
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
