using Cotabby.Core.Spelling;
using Cotabby.Core.Suggestions;
using Cotabby.Core.Tests.Spelling;

namespace Cotabby.Core.Tests.Suggestions;

public sealed class SuggestionCoordinatorWordEndingTests : CoordinatorHarness
{
    private const string Dictionary = "ask 900\nabout 800\nit 700\nplease 500\nschedule 400\nscheduled 20\n";

    public SuggestionCoordinatorWordEndingTests()
        : base(new TypoCorrectionAdvisor(
            TestCorrector.Loaded((SpellingDictionaryLanguage.English, Dictionary)), new SpellingLanguageResolver()))
    {
    }

    [Fact]
    public async Task WithFollowingWordsOff_EachAcceptRevealsTheNextWordWithoutAskingAgain()
    {
        Coordinator.Settings = SuggestionSettings.Default with { ShowFollowingWords = false };
        Type("hello");
        await Shown(" world");
        int requests = Engine.Requests.Count;

        Assert.True(Hook.Press(Keys.Tab()));
        await Shown(" again");

        Assert.Equal(requests, Engine.Requests.Count);
        await Eventually(() => Focus.Current!.Text == "hello world", "only the shown word is inserted");
    }

    [Fact]
    public async Task AnUnknownName_OffersItsEndingAndThenTheWordsAfterIt()
    {
        Engine.Completion = request => request.Prefix.EndsWith("Cota") ? "bby about it" : "";
        Type("ask Cota");
        await Shown("bby");
        int requests = Engine.Requests.Count;

        Assert.True(Hook.Press(Keys.Tab()));
        await Shown(" about it");

        Assert.Equal(requests, Engine.Requests.Count);
    }

    [Fact]
    public async Task AnEmptyResultMidWord_OffersTheDictionaryEnding()
    {
        Engine.Completion = _ => "";
        Type("please schedu");

        await Shown("le");
    }
}
