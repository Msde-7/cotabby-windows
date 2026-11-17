using System.IO;
using Microsoft.Extensions.Logging;

namespace Cotabby.App.Hosting;

/// <summary>
/// Append-only single-file logger, so diagnostics land at a known path without redirecting
/// stdout. There is no batching. Every call writes and flushes under one lock, which keeps lines
/// from different threads whole. Once the file grows past its limit it is renamed to
/// cotabby.old.log, replacing the one before, and a new file starts.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private const long DefaultMaxBytes = 5 * 1024 * 1024;

    private readonly object _gate = new();
    private readonly long _maxBytes;
    private readonly string[] _paths;
    private StreamWriter? _writer;

    /// <summary>
    /// Opens the log. Never throws: when another process holds the file the log goes to
    /// cotabby.alt.log, and when that fails too, nowhere.
    /// </summary>
    public FileLoggerProvider(string path, long maxBytes = DefaultMaxBytes)
    {
        _maxBytes = maxBytes;
        _paths = [path, Path.ChangeExtension(path, ".alt.log")];
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Opening the file below fails too, and the app runs without a log.
        }
        _writer = OpenFirstAvailable();
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
            if (_writer is not { } writer) return;
            try
            {
                writer.WriteLine(line);
                // A tray app can run for weeks, so the log is also rotated while it runs.
                if (writer.BaseStream.Position < _maxBytes) return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A full disk drops the line. Logging must never take the caller down.
                return;
            }
            _writer = null;
            try
            {
                writer.Dispose();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Every line was flushed as it was written, so closing loses nothing.
            }
            _writer = OpenFirstAvailable();
        }
    }

    private StreamWriter? OpenFirstAvailable()
    {
        foreach (var path in _paths)
        {
            if (Open(path) is { } writer) return writer;
        }
        return null;
    }

    private StreamWriter? Open(string path)
    {
        try
        {
            // The log is for diagnosing recent sessions, so it starts over once it has grown large.
            if (File.Exists(path) && new FileInfo(path).Length >= _maxBytes) Rotate(path);
            return new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read))
            {
                AutoFlush = true,
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void Rotate(string path)
    {
        try
        {
            File.Move(path, Path.ChangeExtension(path, ".old.log"), overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Another process holds the file. It keeps growing until a later rotation can rename it.
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
