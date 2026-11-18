using System.Collections.Concurrent;
using Cotabby.Core.Context;
using Cotabby.Core.Focus;
using Cotabby.Core.Tests.Suggestions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cotabby.Core.Tests.Context;

/// <summary>
/// How <see cref="ScreenContextSource"/> keeps one excerpt per field. Captures run on real timers
/// with no settle delay, while freshness is read from a manual clock so expiry is exact.
/// </summary>
public sealed class ScreenContextSourceTests : IDisposable
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly ManualClock _clock = new();
    private readonly FakeReader _reader = new();
    private readonly ConcurrentQueue<FocusedField> _firstExcerpts = new();
    private readonly ConcurrentQueue<FocusedField> _changes = new();
    private readonly List<ScreenContextSource> _sources = [];
    private FocusedField? _current;

    private sealed class FakeReader : IScreenTextReader
    {
        public ConcurrentQueue<FocusedField> Reads { get; } = new();

        /// <summary>What the screen says, ahead of the window title.</summary>
        public volatile string Topic = "budget spreadsheet";

        public Task<ScreenCapture?> ReadAsync(FocusedField field, VisualContextConfiguration configuration, CancellationToken ct)
        {
            Reads.Enqueue(field);
            OcrLine[] lines = [new($"Please review the {Topic} in {field.WindowTitle}", 0.95f)];
            return Task.FromResult<ScreenCapture?>(new ScreenCapture(lines, null, field.WindowTitle));
        }
    }

    private static FocusedField Field(int processId = 100, string fieldId = "composer", string windowTitle = "Inbox", bool secure = false) => new()
    {
        ProcessId = processId,
        ProcessName = "mail",
        Text = "",
        CaretOffset = 0,
        CaretRect = new ScreenRect(10, 10, 1, 20),
        FieldRect = new ScreenRect(0, 0, 400, 200),
        IsSingleLine = false,
        IsSecure = secure,
        FieldId = fieldId,
        WindowTitle = windowTitle,
    };

    private ScreenContextSource Source(TimeSpan? refreshInterval = null)
    {
        var source = new ScreenContextSource(
            _reader, () => _current, NullLogger.Instance, _clock,
            settle: TimeSpan.Zero, refreshInterval: refreshInterval ?? TimeSpan.FromHours(1), lifetime: Lifetime);
        source.FirstExcerptReady += _firstExcerpts.Enqueue;
        source.ExcerptChanged += _changes.Enqueue;
        _sources.Add(source);
        return source;
    }

    public void Dispose()
    {
        foreach (var source in _sources) source.Dispose();
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the screen context.");
            await Task.Delay(5);
        }
    }

    private async Task<string> TrackUntilReady(ScreenContextSource source, FocusedField field)
    {
        _current = field;
        source.Track(field, usesEndpoint: false);
        await WaitUntil(() => source.For(field) is not null);
        return source.For(field)!;
    }

    [Fact]
    public async Task ExcerptIsOnlyReturnedForTheTrackedField()
    {
        var source = Source();
        var field = Field();

        string excerpt = await TrackUntilReady(source, field);

        Assert.Contains("budget spreadsheet", excerpt);
        Assert.Equal(excerpt, source.For(field with { Text = "typed since", CaretOffset = 11 }));
        Assert.Null(source.For(Field(fieldId: "search")));
        Assert.Null(source.For(Field(processId: 200)));
        Assert.Null(source.For(Field(windowTitle: "Archive")));
    }

    [Fact]
    public async Task ExcerptExpiresAfterItsLifetime()
    {
        var source = Source();
        var field = Field();
        await TrackUntilReady(source, field);

        _clock.Advance(Lifetime - TimeSpan.FromMilliseconds(1));
        Assert.NotNull(source.For(field));

        _clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Null(source.For(field));
    }

    [Fact]
    public async Task StopClearsTheExcerpt()
    {
        var source = Source();
        var field = Field();
        await TrackUntilReady(source, field);

        source.Stop();

        Assert.Null(source.For(field));
    }

    [Fact]
    public async Task SecureFieldNeverCaptures()
    {
        var source = Source(refreshInterval: TimeSpan.FromMilliseconds(5));
        var field = Field(secure: true);
        _current = field;

        source.Track(field, usesEndpoint: false);
        await Task.Delay(100);

        Assert.Empty(_reader.Reads);
        Assert.Null(source.For(field));
    }

    [Fact]
    public async Task TrackingASecureFieldStopsTheCurrentSession()
    {
        var source = Source();
        var field = Field();
        await TrackUntilReady(source, field);

        var secure = Field(fieldId: "password", secure: true);
        _current = secure;
        source.Track(secure, usesEndpoint: false);

        Assert.Null(source.For(field));
        Assert.Null(source.For(secure));
    }

    [Fact]
    public async Task FirstExcerptReadyFiresOncePerSession()
    {
        var source = Source(refreshInterval: TimeSpan.FromMilliseconds(5));
        var field = Field();

        await TrackUntilReady(source, field);
        await WaitUntil(() => _reader.Reads.Count >= 3);

        Assert.Equal([field], _firstExcerpts);

        // Tracking the same field again keeps the session rather than starting over.
        source.Track(field, usesEndpoint: false);
        int reads = _reader.Reads.Count;
        await WaitUntil(() => _reader.Reads.Count >= reads + 2);
        Assert.Single(_firstExcerpts);
    }

    [Fact]
    public async Task ADifferentWindowTitleStartsANewSession()
    {
        var source = Source();
        var inbox = Field(windowTitle: "Inbox");
        await TrackUntilReady(source, inbox);

        // A chat app can keep its composer while the conversation changes.
        var archive = Field(windowTitle: "Archive");
        _current = archive;
        source.Track(archive, usesEndpoint: false);
        Assert.Null(source.For(inbox));

        string excerpt = await TrackUntilReady(source, archive);

        Assert.Contains("Archive", excerpt);
        Assert.Equal(["Inbox", "Archive"], _reader.Reads.Select(f => f.WindowTitle));
        Assert.Equal([inbox, archive], _firstExcerpts);
    }

    [Fact]
    public async Task ExcerptChangedFiresOnlyWhenTheTextChanges()
    {
        var source = Source(refreshInterval: TimeSpan.FromMilliseconds(5));
        var field = Field();

        await TrackUntilReady(source, field);
        await WaitUntil(() => _reader.Reads.Count >= 4);
        Assert.Single(_changes);

        _reader.Topic = "quarterly travel plans";
        await WaitUntil(() => _changes.Count == 2);
        int reads = _reader.Reads.Count;
        await WaitUntil(() => _reader.Reads.Count >= reads + 3);
        Assert.Equal(2, _changes.Count);
        Assert.Contains("quarterly travel plans", source.For(field));
    }
}

