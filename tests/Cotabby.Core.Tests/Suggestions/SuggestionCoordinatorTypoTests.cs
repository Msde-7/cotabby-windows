using Cotabby.Core.Input;
using Cotabby.Core.Spelling;
using Cotabby.Core.Suggestions;
using Cotabby.Core.Tests.Spelling;

namespace Cotabby.Core.Tests.Suggestions;

public sealed class SuggestionCoordinatorTypoTests : CoordinatorHarness
{
    private const string Dictionary = "the 1000\nhello 900\nworld 800\nagain 700\n";

    public SuggestionCoordinatorTypoTests() : base(Advisor())
    {
    }

    private static TypoCorrectionAdvisor Advisor()
    {
        var corrector = TestCorrector.Loaded((SpellingDictionaryLanguage.English, Dictionary));
        return new TypoCorrectionAdvisor(corrector, new SpellingLanguageResolver());
    }

    private static SuggestionSettings Typos(bool offer, bool autoFix) => SuggestionSettings.Default with
    {
        Typos = new TypoGateSettings(SuppressCompletionsOnTypo: true, offer, autoFix),
    };

    [Fact]
    public async Task Typo_IsOfferedAndAcceptedAsOneReplacement()
    {
        Type("hello teh ");

        await Shown("the");
        Assert.True(Overlay.ShowsCorrection);
        Assert.True(Hook.Press(Keys.Tab()));
        await Eventually(() => Focus.Current!.Text == "hello the ", "the typo is replaced");
    }

    [Fact]
    public async Task Typo_SuppressesTheCompletionWhenCorrectionsAreNotOffered()
    {
        Coordinator.Settings = Typos(offer: false, autoFix: false);

        Type("hello teh ");

        await StaysHidden();
        // A pause between keys under load may ask about "hello " first, which is fine. Nothing may
        // ask to continue the typo.
        Assert.DoesNotContain(Engine.Requests, r => r.Prefix.EndsWith("teh ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Typo_IsFixedAutomaticallyAfterASpace()
    {
        Coordinator.Settings = Typos(offer: true, autoFix: true);

        Type("hello teh ");

        await Eventually(() => Focus.Current!.Text == "hello the ", "the typo is fixed in place");
        await Shown("world again");
    }

    [Fact]
    public async Task AutomaticFix_IsSkippedWhenTypingHasAlreadyMovedOn()
    {
        Coordinator.Settings = Typos(offer: true, autoFix: true);
        Engine.Completion = _ => "";

        Type("hello teh n");

        await Settled();
        Assert.Equal("hello teh n", Focus.Current!.Text);
    }

    [Fact]
    public async Task TypingPastAnOfferedCorrection_DropsIt()
    {
        Type("hello teh ");
        await Shown("the");
        Engine.Completion = _ => "orld again";

        Type("w");

        await Shown("orld again");
        Assert.False(Overlay.ShowsCorrection);
    }

    [Fact]
    public async Task CorrectlySpelledText_GetsAnOrdinaryCompletion()
    {
        Type("hello the ");

        // The field already ends in a space, so the completion's own leading space is dropped.
        await Shown("world again");
        Assert.False(Overlay.ShowsCorrection);
    }

    [Fact]
    public async Task AQuickSecondPressAfterACorrection_AcceptsTheNextSuggestion()
    {
        Type("hello teh ");
        await Shown("the");

        Assert.True(Hook.Press(Keys.Tab()));
        Assert.True(Hook.Press(Keys.Tab()));

        await Eventually(() => Focus.Current!.Text == "hello the world", "the next word is accepted");
    }

    [Fact]
    public async Task DismissedCorrection_IsNotOfferedAgainForTheSameText()
    {
        Type("hello teh ");
        await Shown("the");
        Hook.Press(Keys.Special(KeyKind.Escape, Keys.VkEscape));
        await Shown("");

        Hook.Press(Keys.Special(KeyKind.Backspace));
        Focus.Replace(Fields.Text("hello teh"));
        Type(" ");

        await StaysHidden();
    }

    [Fact]
    public async Task OfferedCorrection_IsNotAppliedInAnotherField()
    {
        Type("hello teh ");
        await Shown("the");
        Focus.Replace(Fields.Text("other teh ") with { FieldId = "other" });

        Assert.True(Hook.Press(Keys.Tab()));

        await Settled();
        await HoldEnded();
        // Like upstream, the key passes through to the host when the word to correct is gone.
        Assert.Equal("other teh \t", Focus.Current!.Text);
    }

    [Fact]
    public async Task CorrectionTheHostNeverShows_IsNotOfferedAgain()
    {
        Type("hello teh ");
        await Shown("the");
        Focus.IgnoresEdits = true;

        Assert.True(Hook.Press(Keys.Tab()));
        await Shown("");

        // Past the wait for the edit, the old text must not be corrected a second time.
        await StaysHidden();
    }
}
