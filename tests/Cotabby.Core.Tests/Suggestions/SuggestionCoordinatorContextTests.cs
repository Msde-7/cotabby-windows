using Cotabby.Core.Context;
using Cotabby.Core.Focus;
using Cotabby.Core.History;
using Cotabby.Core.Suggestions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cotabby.Core.Tests.Suggestions;

public sealed class SuggestionCoordinatorContextTests : CoordinatorHarness
{
    private const string Agenda = "meeting agenda for Thursday";

    private readonly FakeClipboard _clipboard;

    public SuggestionCoordinatorContextTests() : this(new FakeClipboard(Agenda))
    {
    }

    private SuggestionCoordinatorContextTests(FakeClipboard clipboard) : base(clipboard: clipboard)
    {
        _clipboard = clipboard;
        Engine.Completion = _ => " starts soon";
    }

    private async Task<SuggestionRequest> RequestFor(string prefix)
    {
        SuggestionRequest? Find() => Engine.Requests.LastOrDefault(request => request.Prefix == prefix);
        await Eventually(() => Find() is not null, $"a request for \"{prefix}\" is sent");
        return Find()!;
    }

    private void EnableClipboardContext() =>
        Coordinator.Settings = SuggestionSettings.Default with { ClipboardContext = true };

    [Fact]
    public async Task Clipboard_AFreshRelevantCopyReachesTheRequest()
    {
        EnableClipboardContext();
        Type("the meeting");
        await RequestFor("the meeting");

        _clipboard.Copy($"  {Agenda}\n");
        Type(" now");

        Assert.Equal(Agenda, (await RequestFor("the meeting now")).ClipboardContext);
    }

    [Fact]
    public async Task Clipboard_AFreshCopyUnrelatedToTheTextStaysOut()
    {
        EnableClipboardContext();
        Type("the meeting");
        await RequestFor("the meeting");

        _clipboard.Copy("SELECT * FROM users");
        Type(" now");

        Assert.Equal("", (await RequestFor("the meeting now")).ClipboardContext);
    }

    [Fact]
    public async Task Clipboard_TextCopiedBeforeStartupStaysOut()
    {
        EnableClipboardContext();
        Type("the meeting");
        await RequestFor("the meeting");
        Type(" now");
        await RequestFor("the meeting now");

        Assert.All(Engine.Requests, request => Assert.Equal("", request.ClipboardContext));
    }

    [Fact]
    public async Task Clipboard_IsNeverReadWhileTheSettingIsOff()
    {
        Type("the meeting");
        await RequestFor("the meeting");

        _clipboard.Copy(Agenda);
        Type(" now");

        Assert.Equal("", (await RequestFor("the meeting now")).ClipboardContext);
        Assert.Equal(0, _clipboard.Reads);
    }

    [Fact]
    public async Task Clipboard_AnAcceptedCopyIsNotReadAgainForTheSameHost()
    {
        EnableClipboardContext();
        Type("the meeting");
        await RequestFor("the meeting");
        _clipboard.Copy(Agenda);
        Type(" now");
        await RequestFor("the meeting now");
        int reads = _clipboard.Reads;

        Type("!");

        Assert.Equal(Agenda, (await RequestFor("the meeting now!")).ClipboardContext);
        Assert.Equal(reads, _clipboard.Reads);
    }

    [Fact]
    public async Task Surface_AChatAppIsDescribed()
    {
        Focus.Focus(Fields.Text("", process: "slack") with { WindowTitle = "design - Slack", HelpText = "Message #design" });

        Type("sounds good");

        Assert.Equal(
            ["Format: chat; App: slack; Title: design; Field: Message #design."],
            (await RequestFor("sounds good")).SurfaceLines);
    }

    [Fact]
    public async Task Surface_ACodeEditorIsNot()
    {
        Focus.Focus(Fields.Text("", process: "Code") with { WindowTitle = "Program.cs - Visual Studio Code" });

        Type("returns the");

        Assert.Empty((await RequestFor("returns the")).SurfaceLines);
    }

    [Fact]
    public async Task AnotherConversationInTheSameComposer_DropsTheSuggestion()
    {
        Focus.Focus(Fields.Text("") with { FieldId = "composer", WindowTitle = "Chat with Ann" });
        Engine.Completion = _ => " world again";
        Type("hello");
        await Shown(" world again");

        // The app switches conversations without moving focus.
        Focus.Replace(Focus.Current! with { WindowTitle = "Chat with Bob" });

        await Shown("");
    }

    [Fact]
    public async Task AnEditorsUnsavedMark_KeepsTheSuggestion()
    {
        Focus.Focus(Fields.Text("") with { FieldId = "doc", WindowTitle = "notes - Notepad" });
        Engine.Completion = _ => " world again";
        Type("hello");
        await Shown(" world again");

        Focus.Replace(Focus.Current! with { WindowTitle = "*notes - Notepad" });

        await Task.Delay(400);
        Assert.Equal(" world again", Overlay.Text);
    }

    [Fact]
    public async Task AnAnswerForTheOldConversation_IsNotShownInTheNewOne()
    {
        Focus.Focus(Fields.Text("") with { FieldId = "composer", WindowTitle = "Chat with Ann" });
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Engine.Hold = hold;
        Type("hello");
        await Eventually(() => !Engine.Requests.IsEmpty, "the request is generating");

        Focus.Replace(Focus.Current! with { WindowTitle = "Chat with Bob" });
        hold.SetResult();

        await StaysHidden();
    }

    [Theory]
    [InlineData("(3) Discord", "Discord")]
    [InlineData("[12] general - Slack", "general - Slack")]
    [InlineData("Inbox (3) - Gmail", "Inbox - Gmail")]
    [InlineData("WhatsApp (2)", "WhatsApp")]
    [InlineData("*notes - Notepad", "notes - Notepad")]
    [InlineData("● notes.md - Visual Studio Code", "notes.md - Visual Studio Code")]
    [InlineData("notes.txt • - Sublime Text", "notes.txt - Sublime Text")]
    [InlineData("notes.txt *", "notes.txt")]
    [InlineData("design - Slack", "design - Slack")]
    public void TheTitleKey_LeavesOutUnreadCountsAndUnsavedMarks(string title, string key) =>
        Assert.Equal(key, ScreenContextSource.TitleKey(title));

    [Fact]
    public void TheTitleKey_KeepsNumberedDocumentsApart() =>
        Assert.NotEqual(ScreenContextSource.TitleKey("Report (2).docx - Word"), ScreenContextSource.TitleKey("Report (3).docx - Word"));

    [Fact]
    public async Task AnUnreadCountInTheTitle_KeepsTheSuggestion()
    {
        Focus.Focus(Fields.Text("") with { FieldId = "composer", WindowTitle = "general - Slack" });
        Engine.Completion = _ => " world again";
        Type("hello");
        await Shown(" world again");

        Focus.Replace(Focus.Current! with { WindowTitle = "(1) general - Slack" });
        int reads = Focus.Refreshes;

        await Eventually(
            () => Focus.Refreshes >= reads + 2 || Overlay.Text != " world again", "the suggestion is checked against the field twice");
        await KeysHandled();
        Assert.Equal(" world again", Overlay.Text);
    }

    /// <summary>Holds the first matching read on its way to the UI context, as a slow thread would.</summary>
    private sealed class HoldingRecorder(Func<FocusedField, bool> holds) : ITypingHistoryObserver
    {
        private int _held;

        public ManualResetEventSlim Holding { get; } = new();

        public ManualResetEventSlim Release { get; } = new();

        public void Observe(FocusedField? field, Func<bool> isAllowed)
        {
            if (field is null || !holds(field) || Interlocked.Exchange(ref _held, 1) == 1) return;
            Holding.Set();
            Release.Wait(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task ALateReadWithTheOldTitle_DoesNotTakeTheConversationBack()
    {
        var recorder = new HoldingRecorder(field => field.WindowTitle == "Chat with Ann" && field.Text == "hello");
        Coordinator.HistoryRecorder = recorder;
        Focus.Focus(Fields.Text("") with { FieldId = "composer", WindowTitle = "Chat with Ann" });
        Engine.Completion = r => r.Prefix == "hello there" ? " friend" : "";
        Type("hello");
        Assert.True(recorder.Holding.Wait(TimeSpan.FromSeconds(5)), "a request read the field");

        // The app switches conversations, and typing goes on in the new one.
        Focus.Replace(Focus.Current! with { WindowTitle = "Chat with Bob" });
        Type(" there");
        await Shown(" friend");

        recorder.Release.Set();
        await Settled();
        Assert.Equal(" friend", Overlay.Text);
    }

    private sealed class ScreenReader : IScreenTextReader
    {
        public volatile string Topic = "the budget spreadsheet for the quarterly review";

        public Task<ScreenCapture?> ReadAsync(FocusedField field, VisualContextConfiguration configuration, CancellationToken ct) =>
            Task.FromResult<ScreenCapture?>(new ScreenCapture([new OcrLine($"Please read {Topic} before Friday", 0.95f)], null, null));
    }

    /// <param name="settle">How long focus must stay before the first read, which is never at once in the app.</param>
    private ScreenReader WatchTheScreen(TimeSpan? settle = null)
    {
        var reader = new ScreenReader();
        Coordinator.ScreenContext = new ScreenContextSource(
            reader, () => Focus.Current, NullLogger.Instance, settle: settle ?? TimeSpan.Zero,
            refreshInterval: TimeSpan.FromMilliseconds(20));
        Coordinator.Settings = SuggestionSettings.Default with { ScreenContext = true };
        return reader;
    }

    [Fact]
    public async Task ChangedScreenText_ReplacesTheVisibleSuggestion()
    {
        var reader = WatchTheScreen();
        Engine.Completion = r => r.ScreenContext.Contains("travel") ? " see you there" : " world again";
        Type("hello");
        await Shown(" world again");

        reader.Topic = "the travel plans for the conference in Lisbon";

        await Shown(" see you there");
    }

    [Fact]
    public async Task ScreenTextThatKeepsChanging_AsksAgainOnlyOnceUntilTheNextKey()
    {
        var reader = WatchTheScreen();
        Engine.Completion = r =>
            r.ScreenContext.Contains("travel") ? " see you there"
            : r.ScreenContext.Contains("weather") ? " stay dry"
            : r.ScreenContext.Contains("lunch") ? " at noon"
            : " world again";
        Type("hello");
        await Shown(" world again");

        reader.Topic = "the travel plans for the conference in Lisbon";
        await Shown(" see you there");
        await Settled();
        int requests = Engine.Requests.Count;

        // A clock or a feed beside the field changes again while nobody types.
        reader.Topic = "the weather forecast for the long weekend";
        await Task.Delay(200);
        await Settled();
        Assert.Equal(" see you there", Overlay.Text);
        Assert.Equal(requests, Engine.Requests.Count);

        // A key opens the way for the next change.
        Type(" ");
        await Settled();
        reader.Topic = "the lunch order for the team offsite";
        await Eventually(() => Engine.Requests.Any(r => r.ScreenContext.Contains("lunch")), "the changed screen is asked about");
    }

    [Fact]
    public async Task ChangedScreenText_LeavesAQuickSecondAcceptToTheContinuation()
    {
        var reader = WatchTheScreen();
        Engine.Completion = r => r.Prefix == "hello" ? " world" : " again";
        Engine.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Engine.Holds = r => r.Prefix == "hello world";
        Type("hello");
        await Shown(" world");
        Assert.True(Hook.Press(Backtick));
        await Eventually(() => Focus.Current!.Text == "hello world", "the suggestion is inserted");

        reader.Topic = "the travel plans for the conference in Lisbon";
        await Eventually(() => Engine.Cancelled.Contains("hello world"), "the continuation for the old screen is dropped");

        Assert.True(Hook.Press(Keys.Tab()), "the second press still belongs to the continuation");
    }

    [Fact]
    public async Task ChangedScreenText_WaitsForAKeyTheAppHasNotShownYet()
    {
        var reader = WatchTheScreen();
        Engine.Completion = r => r.ScreenContext.Contains("travel") ? " see you there" : " world again";
        Type("hello");
        await Shown(" world again");

        // The space types through the suggestion, and the app is slow to show it.
        Hook.Press(Keys.Char(' '));
        await Shown("world again");
        reader.Topic = "the travel plans for the conference in Lisbon";
        await Shown("");
        for (int waited = 0; waited < 100 && !Engine.Requests.Any(r => r.ScreenContext.Contains("travel")); waited += 5)
        {
            await Task.Delay(5);
        }
        Focus.Append(" ");

        await Shown("see you there");
        Assert.All(Engine.Requests.Where(r => r.ScreenContext.Contains("travel")), r => Assert.Equal("hello ", r.Prefix));
    }

    [Fact]
    public async Task WithScreenText_TheFirstReadOfTheScreenWarmsTheFieldInsteadOfAPrewarm()
    {
        WatchTheScreen(settle: TimeSpan.FromMilliseconds(20));
        var prewarmed = new System.Collections.Concurrent.ConcurrentQueue<SuggestionRequest>();
        Coordinator.Prewarm = (request, _) =>
        {
            prewarmed.Enqueue(request);
            return Task.CompletedTask;
        };

        Focus.Focus(Fields.Text("Dear team,") with { FieldId = "body" });

        var asked = await RequestFor("Dear team,");
        Assert.Contains("budget", asked.ScreenContext);
        await Settled();
        Assert.Empty(prewarmed);
    }

    [Fact]
    public async Task WithoutScreenText_AFocusedFieldIsStillPrewarmed()
    {
        int prewarms = 0;
        Coordinator.Prewarm = (_, _) =>
        {
            Interlocked.Increment(ref prewarms);
            return Task.CompletedTask;
        };

        Focus.Focus(Fields.Text("Dear team,") with { FieldId = "body" });

        await Eventually(() => Volatile.Read(ref prewarms) == 1, "the field is prewarmed");
    }

    [Fact]
    public async Task WithScreenTextThatCannotBeRead_TheFieldIsStillPrewarmed()
    {
        // OCR is unavailable or the window is protected, so the first read finds nothing.
        Coordinator.ScreenContext = new ScreenContextSource(
            new BlankScreenReader(), () => Focus.Current, NullLogger.Instance, settle: TimeSpan.FromMilliseconds(20),
            refreshInterval: TimeSpan.FromMilliseconds(20));
        Coordinator.Settings = SuggestionSettings.Default with { ScreenContext = true };
        int prewarms = 0;
        Coordinator.Prewarm = (_, _) =>
        {
            Interlocked.Increment(ref prewarms);
            return Task.CompletedTask;
        };

        Focus.Focus(Fields.Text("Dear team,") with { FieldId = "body" });

        await Eventually(() => Volatile.Read(ref prewarms) == 1, "the field is prewarmed");
        await Task.Delay(100);
        Assert.Equal(1, Volatile.Read(ref prewarms));
    }

    private sealed class BlankScreenReader : IScreenTextReader
    {
        public Task<ScreenCapture?> ReadAsync(FocusedField field, VisualContextConfiguration configuration, CancellationToken ct) =>
            Task.FromResult<ScreenCapture?>(null);
    }

    [Fact]
    public async Task UnchangedScreenText_LeavesTheSuggestionAlone()
    {
        WatchTheScreen();
        Engine.Completion = _ => " world again";
        Type("hello");
        await Shown(" world again");
        await Settled();
        int requests = Engine.Requests.Count;

        await Task.Delay(200);

        Assert.Equal(" world again", Overlay.Text);
        Assert.Equal(requests, Engine.Requests.Count);
    }
}

