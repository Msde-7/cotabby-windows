using Cotabby.Core.Spelling;
using Cotabby.Core.Suggestions;
using Cotabby.Core.Tests.Spelling;

namespace Cotabby.Core.Tests.Suggestions;

public sealed class CompletionFinalizerTests
{
    private const string Dictionary =
        "ask 900\nabout 800\nit 700\nagain 600\nplease 500\nschedule 400\nscheduled 20\nthe 1000\n";

    private static readonly TypoCorrectionAdvisor Typos = new(
        TestCorrector.Loaded((SpellingDictionaryLanguage.English, Dictionary)), new SpellingLanguageResolver());

    private static FinalCompletion? Finalize(string completion, string preceding, string trailing = "") =>
        CompletionFinalizer.Finalize(
            completion, Fields.Text(preceding + trailing) with { CaretOffset = preceding.Length },
            SuggestionSettings.Default, spacingIsExact: true, Typos);

    [Fact]
    public void AKnownJoinedWordOffersTheWholeCompletion() =>
        Assert.Equal(new FinalCompletion("dule it again"), Finalize("dule it again", "please sche"));

    [Fact]
    public void AnUnknownNameShowsItsEndingAndKeepsTheFollowingWords()
    {
        var completion = Finalize("bby about it", "ask Cota");

        Assert.Equal(new FinalCompletion("bby", "bby about it"), completion);
        Assert.Equal(3, completion!.FirstOfferLength);
    }

    [Fact]
    public void FollowingWordsThatStartWithATypoAreDropped() =>
        Assert.Equal(new FinalCompletion("bby"), Finalize("bby agian", "ask Cota"));

    [Fact]
    public void AnUnusableResultMidWordFallsBackToALocalWordEnding()
    {
        Assert.Equal(new FinalCompletion("le"), Finalize("", "please schedu"));
        Assert.Equal(new FinalCompletion("le"), Finalize("....", "please schedu"));
    }

    [Fact]
    public void TheLocalWordEndingStaysOutOfAWordThatContinuesAfterTheCaret() =>
        Assert.Null(Finalize("", "please schedu", trailing: "ling"));

    [Fact]
    public void TheLocalWordEndingNeedsAnUnfinishedWord() =>
        Assert.Null(Finalize("", "please "));
}
