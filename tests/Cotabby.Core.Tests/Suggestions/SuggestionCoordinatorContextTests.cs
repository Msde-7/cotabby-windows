using Cotabby.Core.Context;
using Cotabby.Core.Focus;
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

    private sealed class ScreenReader : IScreenTextReader
    {
        public volatile string Topic = "the budget spreadsheet for the quarterly review";

        public Task<ScreenCapture?> ReadAsync(FocusedField field, VisualContextConfiguration configuration, CancellationToken ct) =>
            Task.FromResult<ScreenCapture?>(new ScreenCapture([new OcrLine($"Please read {Topic} before Friday", 0.95f)], null, null));
    }

    private ScreenReader WatchTheScreen()
    {
        var reader = new ScreenReader();
        Coordinator.ScreenContext = new ScreenContextSource(
            reader, () => Focus.Current, NullLogger.Instance, settle: TimeSpan.Zero, refreshInterval: TimeSpan.FromMilliseconds(20));
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

