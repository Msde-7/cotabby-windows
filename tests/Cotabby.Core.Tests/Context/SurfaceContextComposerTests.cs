using Cotabby.Core.Context;

namespace Cotabby.Core.Tests.Context;

public sealed class SurfaceContextComposerTests
{
    private static SurfaceContext? Compose(
        string applicationName = "thunderbird",
        string? processName = "thunderbird",
        bool isIntegratedTerminal = false,
        string? windowTitle = null,
        string? fieldPlaceholder = null) =>
        SurfaceContextComposer.Compose(
            AppSurfaceClassifier.Classify(processName, isIntegratedTerminal),
            applicationName, windowTitle, fieldPlaceholder);

    [Theory]
    [InlineData(AppSurfaceClass.CodeEditor)]
    [InlineData(AppSurfaceClass.Terminal)]
    public void ExcludedSurfaceValues_CannotRenderMetadata(AppSurfaceClass surfaceClass)
    {
        var surface = new SurfaceContext(surfaceClass, "Editor", "Project", "Command");

        Assert.Empty(SurfaceContextComposer.PrefaceLines(surface));
    }

    [Fact]
    public void CodeEditors_GetNoSurfaceContext()
    {
        Assert.Null(Compose(applicationName: "Code", processName: "Code", windowTitle: "Program.cs"));
    }

    [Fact]
    public void Terminals_GetNoSurfaceContext()
    {
        Assert.Null(Compose(applicationName: "WindowsTerminal", processName: "WindowsTerminal", windowTitle: "pwsh"));
        Assert.Null(Compose(processName: "chrome", isIntegratedTerminal: true, windowTitle: "Cloud Shell"));
    }

    [Fact]
    public void AnonymousGenericApp_IsOmitted()
    {
        Assert.Null(Compose(applicationName: "SomeApp", processName: "SomeApp"));
    }

    [Fact]
    public void BlankApplicationName_IsOmitted()
    {
        Assert.Null(Compose(applicationName: "  \n ", windowTitle: "Re: Q3"));
    }

    [Fact]
    public void GenericApp_WithOnlyAPlaceholderIsIncluded()
    {
        var surface = Compose(applicationName: "Obsidian", processName: "Obsidian", fieldPlaceholder: "Write here");

        Assert.Equal(["Format: text; App: Obsidian; Field: Write here."], SurfaceContextComposer.PrefaceLines(surface!));
    }

    [Fact]
    public void GenericApp_WithATitleIsIncluded()
    {
        var surface = Compose(applicationName: "Obsidian", processName: "Obsidian", windowTitle: "Travel plans");

        Assert.Equal(AppSurfaceClass.Other, surface!.SurfaceClass);
        Assert.Equal("Travel plans", surface.WindowTitle);
        Assert.Equal(["Format: text; App: Obsidian; Title: Travel plans."], SurfaceContextComposer.PrefaceLines(surface));
    }

    [Fact]
    public void EmailPreface()
    {
        var surface = Compose(windowTitle: "Re: Q3 budget review");

        Assert.Equal(
            ["Format: email; App: thunderbird; Title: Re: Q3 budget review."],
            SurfaceContextComposer.PrefaceLines(surface!));
    }

    [Fact]
    public void ChatPreface()
    {
        var surface = Compose(applicationName: "slack", processName: "slack", fieldPlaceholder: "Message #design");

        Assert.Equal(
            ["Format: chat; App: slack; Field: Message #design."],
            SurfaceContextComposer.PrefaceLines(surface!));
    }

    [Fact]
    public void BrowserPreface_PreservesAllSanitizedFacts()
    {
        var surface = Compose(
            applicationName: "  Google   Chrome  ",
            processName: "chrome",
            windowTitle: "Planning \"notes\" - Google Chrome",
            fieldPlaceholder: "  Add   a comment  ");

        Assert.Equal(
            ["Format: web text; App: Google Chrome; Title: Planning notes; Field: Add a comment."],
            SurfaceContextComposer.PrefaceLines(surface!));
    }

    [Fact]
    public void Browser_WithoutOptionalFactsKeepsOnlyFormatAndApp()
    {
        var surface = Compose(applicationName: "firefox", processName: "firefox");

        Assert.Equal(["Format: web text; App: firefox."], SurfaceContextComposer.PrefaceLines(surface!));
    }

    [Theory]
    [InlineData("Inbox (3) - Google Chrome", "Google Chrome", "Inbox (3)")]
    [InlineData("Notes — Pages", "Pages", "Notes")]
    [InlineData("Draft – Pages", "Pages", "Draft")]
    [InlineData("Inbox - google chrome", "Google Chrome", "Inbox")]
    [InlineData("Pages - Notes - Pages", "Pages", "Pages - Notes")]
    [InlineData("Pages - Notes", "Pages", "Pages - Notes")]
    public void Title_AppNameSuffixIsStripped(string title, string applicationName, string expected)
    {
        Assert.Equal(expected, SurfaceContextComposer.SanitizedTitle(title, applicationName));
    }

    [Fact]
    public void Title_IsCappedAndWhitespaceCollapsed()
    {
        string longTitle = string.Concat(Enumerable.Repeat("title ", 40));
        Assert.Equal(
            string.Concat(Enumerable.Repeat("title ", 13)) + "ti",
            SurfaceContextComposer.SanitizedTitle(longTitle, "Mail"));

        Assert.Equal("Re: budget review", SurfaceContextComposer.SanitizedTitle("  Re:\n  budget   review ", "Mail"));
    }

    [Fact]
    public void Title_QuotesAndControlCharactersAreDropped()
    {
        Assert.Equal("Say hello there", SurfaceContextComposer.SanitizedTitle("Say \"hello\"\u0007 there", "Mail"));
    }

    [Fact]
    public void EmptyTitle_BecomesNull()
    {
        Assert.Null(SurfaceContextComposer.SanitizedTitle("   ", "Mail"));
        Assert.Null(SurfaceContextComposer.SanitizedTitle(null, "Mail"));
    }

    [Fact]
    public void Placeholder_IsSanitizedAndCapped()
    {
        Assert.Equal("Write your reply", Compose(fieldPlaceholder: "  Write \"your\"\u0007   reply  ")!.FieldPlaceholder);
        Assert.Equal(new string('p', 60), Compose(fieldPlaceholder: new string('p', 70))!.FieldPlaceholder);
    }
}
