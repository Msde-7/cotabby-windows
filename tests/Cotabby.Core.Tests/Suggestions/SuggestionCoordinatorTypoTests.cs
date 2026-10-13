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
        Assert.Empty(Engine.Requests);
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
}
