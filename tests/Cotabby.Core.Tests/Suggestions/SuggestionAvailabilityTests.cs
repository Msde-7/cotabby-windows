using Cotabby.Core.Focus;
using Cotabby.Core.Input;
using Cotabby.Core.Suggestions;

namespace Cotabby.Core.Tests.Suggestions;

public sealed class SuggestionAvailabilityTests
{
    private const string TurnedOff = "Cotabby is turned off.";
    private const string Paused = "Cotabby is temporarily paused.";
    private const string BatterySaver = "Cotabby is paused because battery saver is on.";
    private const string TerminalApp = "Cotabby is not available in terminal apps.";
    private const string IntegratedTerminal = "Cotabby is not available in the integrated terminal.";
    private const string SecureField = "Secure fields are left alone.";

    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_000_000);
    private static readonly SuggestionSettings Settings = SuggestionSettings.Default;
    private static readonly SuggestionSettings PausedSettings =
        Settings with { Pause = PauseState.For(PauseDuration.Indefinitely, Now) };

    private static readonly FocusedField XtermField =
        Fields.Text("ls", process: "Code") with { ClassName = "xterm-helper-textarea" };

    private static SuggestionSettings Blocking(params string[] apps) =>
        Settings with { BlockedApps = new HashSet<string>(apps, StringComparer.OrdinalIgnoreCase) };

    [Fact]
    public void DisabledReason_EverythingAllowed_IsNull()
    {
        Assert.Null(SuggestionAvailability.DisabledReason(Settings, Now, batterySaverOn: false));
        Assert.Null(SuggestionAvailability.DisabledReason(Settings, Fields.Text("hello")));
    }

    [Fact]
    public void DisabledReason_GloballyDisabled() =>
        Assert.Equal(
            TurnedOff,
            SuggestionAvailability.DisabledReason(Settings with { Enabled = false }, Now, batterySaverOn: false));

    [Fact]
    public void DisabledReason_PausedOnlyWhileThePauseIsActive()
    {
        Assert.Equal(Paused, SuggestionAvailability.DisabledReason(PausedSettings, Now, batterySaverOn: false));
        Assert.Equal(
            Paused,
            SuggestionAvailability.DisabledReason(
                Settings with { Pause = new PauseState(Now.AddMinutes(1)) }, Now, batterySaverOn: false));
        Assert.Null(
            SuggestionAvailability.DisabledReason(Settings with { Pause = new PauseState(Now) }, Now, batterySaverOn: false));
    }

    [Theory]
    [InlineData(true, true, BatterySaver)]
    [InlineData(true, false, null)]
    [InlineData(false, true, null)]
    public void DisabledReason_BatterySaverNeedsBothTheSystemStateAndTheSetting(
        bool batterySaverOn, bool pauseOnBatterySaver, string? expected) =>
        Assert.Equal(
            expected,
            SuggestionAvailability.DisabledReason(
                Settings with { PauseOnBatterySaver = pauseOnBatterySaver }, Now, batterySaverOn));

    [Fact]
    public void DisabledReason_EarlierGlobalGateWins()
    {
        Assert.Equal(
            TurnedOff,
            SuggestionAvailability.DisabledReason(PausedSettings with { Enabled = false }, Now, batterySaverOn: true));
        Assert.Equal(Paused, SuggestionAvailability.DisabledReason(PausedSettings, Now, batterySaverOn: true));
    }

    [Fact]
    public void DisabledReason_BlockedApp_NamesTheProcess() =>
        Assert.Equal(
            "Cotabby is disabled in chrome.",
            SuggestionAvailability.DisabledReason(Blocking("chrome"), Fields.Text("hello", process: "chrome")));

    [Fact]
    public void DisabledReason_BlockedApp_OnlyMatchesTheFocusedProcess() =>
        Assert.Null(SuggestionAvailability.DisabledReason(Blocking("chrome"), Fields.Text("hello", process: "notepad")));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DisabledReason_TerminalApp_IsBlockedRegardlessOfTheIntegratedTerminalSetting(bool suggestInIntegrated) =>
        Assert.Equal(
            TerminalApp,
            SuggestionAvailability.DisabledReason(
                Settings with { SuggestInIntegratedTerminals = suggestInIntegrated },
                Fields.Text("ls", process: "WindowsTerminal")));

    [Fact]
    public void DisabledReason_IntegratedTerminal_IsBlockedUntilOptedIn()
    {
        Assert.Equal(IntegratedTerminal, SuggestionAvailability.DisabledReason(Settings, XtermField));
        Assert.Null(
            SuggestionAvailability.DisabledReason(Settings with { SuggestInIntegratedTerminals = true }, XtermField));
    }

    [Fact]
    public void DisabledReason_SecureField() =>
        Assert.Equal(
            SecureField,
            SuggestionAvailability.DisabledReason(Settings, Fields.Text("hunter2") with { IsSecure = true }));

    [Fact]
    public void DisabledReason_CredentialField() =>
        Assert.Equal(
            CredentialFieldDetector.BlockedReason,
            SuggestionAvailability.DisabledReason(
                Settings, Fields.Text("alice", singleLine: true) with { Label = "Email or phone" }));

    [Fact]
    public void DisabledReason_SelectedText() =>
        Assert.Equal(
            "Text is selected.",
            SuggestionAvailability.DisabledReason(Settings, Fields.Text("hello") with { HasSelection = true }));

    [Fact]
    public void DisabledReason_CotabbysOwnWindows() =>
        Assert.Equal(
            "Cotabby does not suggest in its own windows.",
            SuggestionAvailability.DisabledReason(Settings, Fields.Text("hello", pid: Environment.ProcessId)));

    [Fact]
    public void DisabledReason_EarlierFieldGateWins()
    {
        var terminal = Fields.Text("ls", process: "WindowsTerminal", singleLine: true) with
        {
            ClassName = "xterm-screen",
            IsSecure = true,
            Label = "Password",
            HasSelection = true,
        };
        var xterm = terminal with { ProcessName = "Code" };
        var secure = xterm with { ClassName = "" };
        var credential = secure with { IsSecure = false };

        Assert.Equal(
            "Cotabby is disabled in WindowsTerminal.",
            SuggestionAvailability.DisabledReason(Blocking("WindowsTerminal"), terminal));
        Assert.Equal(TerminalApp, SuggestionAvailability.DisabledReason(Settings, terminal));
        Assert.Equal(IntegratedTerminal, SuggestionAvailability.DisabledReason(Settings, xterm));
        Assert.Equal(SecureField, SuggestionAvailability.DisabledReason(Settings, secure));
        Assert.Equal(CredentialFieldDetector.BlockedReason, SuggestionAvailability.DisabledReason(Settings, credential));
    }

    [Theory]
    [InlineData(KeyKind.Character, true)]
    [InlineData(KeyKind.Backspace, true)]
    [InlineData(KeyKind.Enter, true)]
    [InlineData(KeyKind.Tab, false)]
    [InlineData(KeyKind.Escape, false)]
    [InlineData(KeyKind.Delete, false)]
    [InlineData(KeyKind.Arrow, false)]
    [InlineData(KeyKind.Other, false)]
    public void SchedulesPrediction_OnlyForUnmodifiedKeyDownsThatEditText(KeyKind kind, bool expected)
    {
        var press = Keys.Special(kind);

        Assert.Equal(expected, SuggestionAvailability.SchedulesPrediction(press));
        Assert.False(SuggestionAvailability.SchedulesPrediction(press with { IsKeyDown = false }));
        Assert.False(SuggestionAvailability.SchedulesPrediction(press with { HasNonShiftModifier = true }));
    }

    [Theory]
    [InlineData(KeyKind.Escape, true)]
    [InlineData(KeyKind.Enter, true)]
    [InlineData(KeyKind.Arrow, true)]
    [InlineData(KeyKind.Delete, true)]
    [InlineData(KeyKind.Backspace, true)]
    [InlineData(KeyKind.Character, false)]
    [InlineData(KeyKind.Tab, false)]
    [InlineData(KeyKind.Other, false)]
    public void ClearsSuggestion_OnlyForKeyDownsThatDoNotTypeThroughIt(KeyKind kind, bool expected)
    {
        var press = Keys.Special(kind);

        Assert.Equal(expected, SuggestionAvailability.ClearsSuggestion(press));
        Assert.False(SuggestionAvailability.ClearsSuggestion(press with { IsKeyDown = false }));
    }

    [Theory]
    [InlineData("hello", 5, true)]
    [InlineData("hello ", 6, true)]
    [InlineData("", 0, false)]
    [InlineData(" \n ", 3, false)]
    [InlineData("  hello", 2, false)]
    [InlineData("hello", 99, true)]
    public void HasTextToContinue_NeedsNonWhitespaceBeforeTheCaret(string text, int caret, bool expected) =>
        Assert.Equal(expected, SuggestionAvailability.HasTextToContinue(Fields.Text(text) with { CaretOffset = caret }));
}
