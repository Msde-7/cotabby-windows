using System.IO;
using Cotabby.App.Hosting;
using Microsoft.Extensions.Logging;

namespace Cotabby.Inference.Tests;

public sealed class FileLoggerProviderTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"cotabby-log-{Guid.NewGuid():N}");
    private string LogPath => Path.Combine(_directory, "cotabby.log");

    public FileLoggerProviderTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void LogHeldByAnotherProcess_DoesNotThrowAndTheLineLandsElsewhere()
    {
        File.WriteAllText(LogPath, "held");
        using (new FileStream(LogPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            using var provider = new FileLoggerProvider(LogPath);
            provider.CreateLogger("Test").LogInformation("hello");
        }

        Assert.Equal("held", File.ReadAllText(LogPath));
        Assert.Contains(Directory.GetFiles(_directory), file => file != LogPath && File.ReadAllText(file).Contains("hello"));
    }

    [Fact]
    public void CurrentPath_IsTheFileTheLinesGoTo()
    {
        using (var provider = new FileLoggerProvider(LogPath))
        {
            Assert.Equal(LogPath, provider.CurrentPath);
        }

        string? fallback;
        using (new FileStream(LogPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        using (var provider = new FileLoggerProvider(LogPath))
        {
            provider.CreateLogger("Test").LogInformation("hello");
            fallback = provider.CurrentPath;
        }

        Assert.NotNull(fallback);
        Assert.NotEqual(LogPath, fallback);
        Assert.Contains("hello", File.ReadAllText(fallback));
    }

    [Fact]
    public void CurrentPath_IsNullOnceDisposed()
    {
        var provider = new FileLoggerProvider(LogPath);
        provider.Dispose();

        Assert.Null(provider.CurrentPath);
    }

    [Fact]
    public void LargeLogHeldByAnotherProcess_DoesNotThrow()
    {
        File.WriteAllText(LogPath, new string('x', 200));
        using var held = new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.Read);

        var exception = Record.Exception(() => new FileLoggerProvider(LogPath, maxBytes: 100).Dispose());

        Assert.Null(exception);
    }

    [Fact]
    public void LargeLog_IsRotatedAtStartup()
    {
        File.WriteAllText(LogPath, new string('x', 200));

        using (var provider = new FileLoggerProvider(LogPath, maxBytes: 100))
        {
            provider.CreateLogger("Test").LogInformation("fresh");
        }

        Assert.DoesNotContain("xxx", File.ReadAllText(LogPath));
        Assert.Contains("fresh", File.ReadAllText(LogPath));
        Assert.Contains(Directory.GetFiles(_directory), file => File.ReadAllText(file) == new string('x', 200));
    }

    [Fact]
    public void FailedRotation_IsRetriedOnlyAfterAWhile()
    {
        var time = new ManualTime();
        File.WriteAllText(LogPath, new string('x', 200));
        string oldLog = Path.ChangeExtension(LogPath, ".old.log");
        // Another process reads the log without letting it be renamed.
        var held = new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        using var provider = new FileLoggerProvider(LogPath, maxBytes: 100, time);
        var logger = provider.CreateLogger("Test");
        logger.LogInformation("while held");
        held.Dispose();

        // The file can be renamed now, but each line must not try again.
        logger.LogInformation("released");
        Assert.False(File.Exists(oldLog));

        time.Advance(TimeSpan.FromMinutes(2));
        logger.LogInformation("later");
        Assert.True(File.Exists(oldLog));
    }

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    [Fact]
    public void LongSession_RotatesOnceTheLogGrowsLarge()
    {
        using (var provider = new FileLoggerProvider(LogPath, maxBytes: 1000))
        {
            var logger = provider.CreateLogger("Test");
            for (int i = 0; i < 100; i++) logger.LogInformation("line {Index}", i);
        }

        Assert.True(new FileInfo(LogPath).Length <= 1000);
        Assert.Contains("line 99", File.ReadAllText(LogPath));
        Assert.Equal(2, Directory.GetFiles(_directory).Length);
    }
}
