using System.Collections.Concurrent;
using Cotabby.Core.Focus;
using Cotabby.Core.History;
using Cotabby.Core.Suggestions;

namespace Cotabby.Core.Tests.Suggestions;

public sealed class SuggestionCoordinatorHistoryTests : CoordinatorHarness
{
    private sealed class FakeHistory : ISuggestionHistoryProvider, ITypingHistoryObserver
    {
        public ConcurrentQueue<(FocusedField? Field, bool Allowed)> Observed { get; } = new();

        public IReadOnlyList<string> HistoryExamples(FocusedField field, bool usesEndpoint) =>
            usesEndpoint ? [] : ["An earlier sentence."];

        public string? PhraseContinuation(SuggestionRequest request, bool usesEndpoint) => null;

        public void Observe(FocusedField? field, Func<bool> isAllowed) => Observed.Enqueue((field, isAllowed()));
    }

    private readonly FakeHistory _history = new();

    public SuggestionCoordinatorHistoryTests()
    {
        Coordinator.History = _history;
        Coordinator.HistoryRecorder = _history;
    }

    [Fact]
    public async Task HistoryExamplesReachTheRequest()
    {
        Type("Hello there");

        await Eventually(() => Engine.Requests.Any(r => r.Prefix == "Hello there"), "a request is sent");
        Assert.Equal(["An earlier sentence."], Engine.Requests.Last().HistoryExamples);
    }

    [Fact]
    public async Task EachFreshReadIsRecordedWithTheAvailabilityRule()
    {
        Type("Hello there");

        await Eventually(
            () => _history.Observed.Any(o => o.Field?.PrecedingText == "Hello there" && o.Allowed),
            "the typed text is observed");
    }

    [Fact]
    public async Task RecordingIsNotAllowedWhereSuggestionsAreOff()
    {
        Coordinator.Settings = SuggestionSettings.Default with
        {
            BlockedApps = new HashSet<string>(["notepad"], StringComparer.OrdinalIgnoreCase),
        };
        Type("Hello there");

        await Eventually(() => _history.Observed.Any(o => o.Field?.PrecedingText == "Hello there"), "the text is observed");
        Assert.DoesNotContain(_history.Observed, o => o.Field?.PrecedingText == "Hello there" && o.Allowed);
    }

    [Fact]
    public async Task LeavingEveryFieldFinishesTheRecording()
    {
        Focus.Focus(null);

        await Eventually(() => _history.Observed.Any(o => o.Field is null), "focus leaving is observed");
    }
}
