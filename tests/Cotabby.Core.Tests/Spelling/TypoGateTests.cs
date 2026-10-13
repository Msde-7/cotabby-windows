using Cotabby.Core.Spelling;

namespace Cotabby.Core.Tests.Spelling;

/// <summary>Spell-check behavior is stubbed, so each case pins one branch of the decision order.</summary>
public class TypoGateTests
{
    private static readonly Dictionary<string, string> NameCorrection = new() { ["nmae"] = "name" };

    private static TypoGateDecision Resolve(
        string precedingText,
        bool suppress,
        bool offer,
        bool automatic = false,
        Dictionary<string, string>? corrections = null,
        params string[] typos) =>
        TypoGate.Resolve(
            precedingText,
            new TypoGateSettings(suppress, offer, automatic),
            typos.Contains,
            word => corrections?.GetValueOrDefault(word));

    [Theory]
    [InlineData("wri")]
    [InlineData("becau")]
    [InlineData("recomm")]
    [InlineData("car")]
    public void PausedPrefixNeverCallsSpellingOrCorrection(string prefix)
    {
        var decision = TypoGate.Resolve(
            "I typed " + prefix,
            new TypoGateSettings(true, true, true),
            _ => throw new InvalidOperationException("Unfinished words must not be spell-checked"),
            _ => throw new InvalidOperationException("Unfinished words must not be corrected"));
        Assert.Equal(TypoGateDecision.Proceed, decision);
    }

    [Fact]
    public void ProceedsWhenSuppressionDisabled()
    {
        Assert.Equal(TypoGateDecision.Proceed, Resolve("hi nmae", suppress: false, offer: true, typos: "nmae"));
        Assert.Equal(TypoGateDecision.Proceed, Resolve("hi nmae ", suppress: false, offer: true, typos: "nmae"));
    }

    [Fact]
    public void ProceedsWhenTrailingTokenIsNotAWord()
    {
        Assert.Equal(TypoGateDecision.Proceed, Resolve("ping 99 ", suppress: true, offer: true, typos: "99"));
    }

    [Fact]
    public void ProceedsWhenCommittedWordIsNotATypo()
    {
        // The trailing space commits "name", so the gate does consult the checker, which says no.
        var checkedWords = new List<string>();
        var decision = TypoGate.Resolve(
            "hi name ",
            new TypoGateSettings(true, true, false),
            word =>
            {
                checkedWords.Add(word);
                return false;
            },
            _ => throw new InvalidOperationException("A correctly spelled word must not be corrected"));
        Assert.Equal(TypoGateDecision.Proceed, decision);
        Assert.Equal(["name"], checkedWords);
    }

    [Fact]
    public void SuppressesWhenTypoAndCorrectionsOff()
    {
        Assert.Equal(TypoGateDecision.Suppress, Resolve("hi nmae ", suppress: true, offer: false, typos: "nmae"));
        Assert.Equal(
            TypoGateDecision.Suppress,
            Resolve("hi nmae ", suppress: true, offer: false, corrections: NameCorrection, typos: "nmae"));
    }

    [Fact]
    public void SuppressesWhenTypoButNoCorrectionAvailable()
    {
        Assert.Equal(TypoGateDecision.Suppress, Resolve("hi nmae ", suppress: true, offer: true, typos: "nmae"));
    }

    [Fact]
    public void PunctuationDelimiterOffersButNeverAutoApplies()
    {
        // A comma commits the word, but automatic fixing is reserved for a bare trailing space.
        Assert.Equal(
            TypoGateDecision.OfferCorrection("nmae", "name"),
            Resolve("hi my nmae,", suppress: true, offer: true, automatic: true, NameCorrection, "nmae"));
    }

    [Fact]
    public void AutomaticOnlyWithNonSpaceDelimiterSuppresses()
    {
        Assert.Equal(
            TypoGateDecision.Suppress,
            Resolve("hi my nmae,", suppress: true, offer: false, automatic: true, NameCorrection, "nmae"));
    }

    [Fact]
    public void CorrectsWhenTypoFollowedByOneSpace()
    {
        Assert.Equal(
            TypoGateDecision.OfferCorrection("nmae", "name"),
            Resolve("hi my nmae ", suppress: true, offer: true, corrections: NameCorrection, typos: "nmae"));
    }

    [Fact]
    public void ProceedsWhenTypoFollowedByTwoSpaces()
    {
        Assert.Equal(
            TypoGateDecision.Proceed,
            Resolve("hi my nmae  ", suppress: true, offer: true, corrections: NameCorrection, typos: "nmae"));
    }

    [Fact]
    public void AutomaticFixAppliesOnlyAfterSpace()
    {
        Assert.Equal(
            TypoGateDecision.ApplyCorrection("nmae", "name"),
            Resolve("hi my nmae ", suppress: true, offer: false, automatic: true, NameCorrection, "nmae"));
    }

    [Fact]
    public void AutomaticFixDoesNotMutateUnfinishedWord()
    {
        Assert.Equal(
            TypoGateDecision.Proceed,
            Resolve("hi my nmae", suppress: true, offer: true, automatic: true, NameCorrection, "nmae"));
    }
}
