using Cotabby.Core.Metrics;

namespace Cotabby.Core.Tests.Metrics;

public sealed class RequestLogTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"cotabby-requests-{Guid.NewGuid():N}.json");

    public void Dispose() => File.Delete(_path);

    [Fact]
    public void KeepsOnlyTheMostRecentRequests()
    {
        var log = new RequestLog();
        for (int i = 0; i < RequestLog.Capacity + 5; i++) log.Record("Cotabby Mini", i);

        Assert.Equal(RequestLog.Capacity, log.Entries.Count);
        Assert.Equal(5, log.Entries[0].LatencyMs);
        Assert.Equal(RequestLog.Capacity + 4, log.Entries[^1].LatencyMs);
    }

    [Fact]
    public void SurvivesARestart()
    {
        var at = new DateTimeOffset(2026, 11, 2, 9, 30, 0, TimeSpan.Zero);
        new RequestLog(_path).Record("Cotabby Base", 142, at);

        Assert.Equal([new RequestLogEntry(at, "Cotabby Base", 142)], new RequestLog(_path).Entries);
    }

    [Fact]
    public void ClearEmptiesTheLogAndItsFile()
    {
        var log = new RequestLog(_path);
        log.Record("Cotabby Base", 142);
        int changes = 0;
        log.Changed += () => changes++;

        log.Clear();
        log.Clear();

        Assert.Empty(new RequestLog(_path).Entries);
        Assert.Equal(1, changes);
    }

    [Fact]
    public void ADamagedFileStartsEmpty()
    {
        File.WriteAllText(_path, "{ not json");

        Assert.Empty(new RequestLog(_path).Entries);
    }
}
