using Cotabby.Core.Suggestions;

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
}
