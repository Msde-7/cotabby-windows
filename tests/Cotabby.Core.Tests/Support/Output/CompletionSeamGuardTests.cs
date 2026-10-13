using Cotabby.Core.Support;
using Assessment = Cotabby.Core.Support.CompletionSeamGuard.SpellingAssessment;

namespace Cotabby.Core.Tests;

/// <summary>
/// The seam guard must fire only on the failure shapes it exists for and never on the ordinary
/// continuations that surround them, so most of these are allow cases.
/// </summary>
public class CompletionSeamGuardTests
{
    private static readonly Func<string, Assessment> KnowsEverything = _ => Assessment.Known;
    private static readonly Func<string, Assessment> KnowsNothing = _ => Assessment.UncorrectableTypo;
    private static readonly Func<string, Assessment> AllCorrectable = _ => Assessment.CorrectableTypo;

    /// <summary>The listed words are known and everything else is an uncorrectable typo.</summary>
    private static Func<string, Assessment> Knowing(params string[] words) =>
        word => words.Contains(word.ToLowerInvariant()) ? Assessment.Known : Assessment.UncorrectableTypo;

    /// <summary>The listed words are known and everything else is a correctable typo.</summary>
    private static Func<string, Assessment> KnownElseCorrectable(params string[] words) =>
        word => words.Contains(word) ? Assessment.Known : Assessment.CorrectableTypo;

    /// <summary>Only the given word is a correctable typo.</summary>
    private static Func<string, Assessment> Correctable(string typo) =>
        word => word == typo ? Assessment.CorrectableTypo : Assessment.Known;

    private static Func<string, Assessment> MustNotBeCalled(string reason) =>
        _ => throw new Xunit.Sdk.XunitException(reason);

    private static string? Visible(string precedingText, string completion, Func<string, Assessment> spelling) =>
        CompletionSeamGuard.VisibleText(precedingText, completion, spelling);

    private static void AssertShownUnchanged(string precedingText, string completion, Func<string, Assessment> spelling) =>
        Assert.Equal(completion, Visible(precedingText, completion, spelling));

    private static void AssertSuppressed(string precedingText, string completion, Func<string, Assessment> spelling) =>
        Assert.Null(Visible(precedingText, completion, spelling));

    [Fact]
    public void FreshPunctuationRunIsSuppressed()
    {
        AssertSuppressed("Wait", " what....", KnowsEverything);
    }

    [Fact]
    public void SymbolRunIsSuppressed()
    {
        AssertSuppressed("Price: ", "$$$$", KnowsEverything);
    }

    [Fact]
    public void ThreeCharacterRunIsAllowed()
    {
        AssertShownUnchanged("Well", "... maybe", KnowsEverything);
    }

    [Fact]
    public void SingleTrailingCharacterDoesNotExemptAJunkRun()
    {
        // Only a real preceding run of two or more reads as a divider being extended.
        AssertSuppressed("Hello.", "....", KnowsEverything);
    }

    [Fact]
    public void ContinuingAnExistingDividerIsAllowed()
    {
        AssertShownUnchanged("----", "------", KnowsEverything);
    }

    [Fact]
    public void FreshDividerAwayFromSeamIsSuppressed()
    {
        AssertSuppressed("----", " section ======", KnowsEverything);
    }

    [Fact]
    public void RepeatedLettersAreNotJunk()
    {
        AssertShownUnchanged("That is so", " coooool", KnowsEverything);
    }

    [Fact]
    public void MisspelledSeamWordIsSuppressed()
    {
        AssertSuppressed("I am so gre", "atful for this", AllCorrectable);
    }

    [Fact]
    public void KnownSeamWordIsAllowed()
    {
        AssertShownUnchanged("I am so gre", "at to hear it", Knowing("great"));
    }

    [Fact]
    public void SeamRuleOnlyAppliesMidWord()
    {
        AssertShownUnchanged("I am so ", "greatful", KnowsNothing);
    }

    [Fact]
    public void CapitalizedSeamWordIsAllowed()
    {
        // Names and brands are routinely out of dictionary, so the unknown word keeps its ending.
        Assert.Equal("bby", Visible("Ask Cota", "bby about it", KnowsNothing));
    }

    [Fact]
    public void ShortSeamWordIsAllowed()
    {
        Assert.Equal("t", Visible("a", "t the office", KnowsNothing));
    }

    [Fact]
    public void DigitAdjacentSeamIsAllowed()
    {
        AssertShownUnchanged("version 2", "024 release", KnowsNothing);
    }

    [Fact]
    public void CjkSeamIsAllowed()
    {
        AssertShownUnchanged("これはとても良", "い天気ですね", KnowsNothing);
    }

    [Fact]
    public void OrdinaryContinuationIsAllowed()
    {
        AssertShownUnchanged("Thanks again for your help", " with the move last weekend.", Knowing("with"));
    }

    [Fact]
    public void MisspelledLeadingWordWithCorrectionIsSuppressed()
    {
        AssertSuppressed("Je veux ", "ecrir plus vite", Correctable("ecrir"));
    }

    [Fact]
    public void LeadingWordWithoutCorrectionIsAllowed()
    {
        // An unknown name or domain term stays visible when the checker has no suggestion for it.
        AssertShownUnchanged("Use ", "cotabby avec soin", word => word == "cotabby" ? Assessment.UncorrectableTypo : Assessment.Known);
    }

    [Fact]
    public void CapitalizedLeadingWordIsAllowed()
    {
        AssertShownUnchanged("Ask ", "Cotypist about it", MustNotBeCalled("capitalized leading words bypass spelling"));
    }

    [Fact]
    public void MidWordCompletionOnlyAssessesTheJoinedSeamWord()
    {
        AssertShownUnchanged("Je veux ecr", "irregular", word =>
        {
            Assert.Equal("ecrirregular", word);
            return Assessment.Known;
        });
    }

    [Fact]
    public void QuotedLeadingWordIsSuppressed()
    {
        AssertSuppressed("Il répond ", "“ecrir” plus vite", Correctable("ecrir"));
    }

    [Fact]
    public void ParenthesizedLeadingWordAfterTextIsSuppressed()
    {
        AssertSuppressed("Il répond", ": (ecrir) plus vite", Correctable("ecrir"));
    }

    [Fact]
    public void ContractionIsAssessedAsOneWord()
    {
        AssertShownUnchanged("It ", "doesn't matter", word =>
        {
            Assert.Equal("doesn't", word);
            return Assessment.Known;
        });
    }

    [Fact]
    public void LetterAndDigitLeadingTokenIsAllowedWithoutSpellLookup()
    {
        AssertShownUnchanged("Use ", "ecrir2 here", MustNotBeCalled("letter-and-digit tokens bypass spelling"));
    }

    [Fact]
    public void CorrectableLeadingWordIsSuppressedWithoutTrailingBoundary()
    {
        AssertSuppressed("Je veux ", "ecrir", Correctable("ecrir"));
    }

    [Fact]
    public void ConnectorContinuationOfTheCaretWordSkipsTheLeadingWordRule()
    {
        AssertShownUnchanged("I don", "'t know", word =>
        {
            Assert.Equal("don't", word);
            return Assessment.Known;
        });
    }

    [Fact]
    public void HyphenatedLeadingWordIsAssessedAsOneToken()
    {
        AssertShownUnchanged("A ", "state-of-the-art tool", word =>
        {
            Assert.Equal("state-of-the-art", word);
            return Assessment.Known;
        });
    }

    [Fact]
    public void ShortLeadingWordIsAllowedWithoutSpellLookup()
    {
        // Words under four letters are too ambiguous to judge, so even "teh" passes.
        AssertShownUnchanged("Send ", "teh report", MustNotBeCalled("short leading words bypass spelling"));
    }

    [Fact]
    public void RepeatedDigitsAreNotJunk()
    {
        AssertShownUnchanged("PIN hint: ", "0000 then 1111", KnowsEverything);
    }

    [Fact]
    public void CamelCaseLeadingTokenBypassesSpelling()
    {
        AssertShownUnchanged("Set ", "myVaraible here", MustNotBeCalled("camelCase identifiers bypass spelling"));
    }

    [Fact]
    public void MissingSeparatorBetweenKnownWordsIsRepaired()
    {
        foreach (string prefix in new[] { "up", "delay" })
        {
            Assert.Equal(
                " and it takes time",
                Visible("predictions come " + prefix, "and it takes time", KnownElseCorrectable(prefix, "and")));
        }
    }

    [Fact]
    public void SeparatorRepairPreservesValidJoinsAndRejectsUnknownPieces()
    {
        AssertShownUnchanged("a car", "pet on the floor", Knowing("car", "pet", "carpet"));
        foreach (string known in new[] { "gre", "atful" })
        {
            AssertSuppressed("gre", "atful for this", KnownElseCorrectable(known));
        }
        // Unknown joins must not be rewritten.
        Assert.Equal("bar", Visible("foo", "bar next", Knowing("foo", "bar")));
    }

    [Fact]
    public void SeparatorRepairDoesNotSplitContractions()
    {
        AssertSuppressed("don", "'t go", Correctable("don't"));
        // A trailing apostrophe is still the contraction being typed, not a finished word.
        AssertSuppressed("they said don'", "t go", KnownElseCorrectable("don'", "t"));
    }

    [Fact]
    public void SeparatorRepairTreatsWholeContractionsAsWords()
    {
        Assert.Equal(
            " we've decided to move forward",
            Visible("let you know that", "we've decided to move forward", KnownElseCorrectable("that", "we've")));
        Assert.Equal(
            " going to be fine",
            Visible("I think it’s", "going to be fine", KnownElseCorrectable("it’s", "going")));
    }

    [Fact]
    public void AbandonedFragmentIsSuppressedEvenIfTheDictionaryRecognizesIt()
    {
        AssertSuppressed("book a roo", " room for two guests", KnowsEverything);
        AssertShownUnchanged("a car", " is parked", KnowsEverything);
    }

    [Fact]
    public void AbandonedFragmentDependsOnWhetherItIsACorrectableTypo()
    {
        AssertSuppressed("the cta", " is here", Correctable("cta"));
        // An uncorrectable fragment (a name, jargon) may legitimately be finished by the writer.
        AssertShownUnchanged(
            "the cta", " is here", word => word == "cta" ? Assessment.UncorrectableTypo : Assessment.Known);
    }

    [Fact]
    public void KnownJoinedWordKeepsItsPhraseAtEveryPrefixLength()
    {
        const string word = "build";
        for (int prefixLength = 1; prefixLength < word.Length; prefixLength++)
        {
            AssertShownUnchanged(
                "I would like to " + word[..prefixLength], word[prefixLength..] + " a spaceship", Knowing(word));
        }
    }

    [Fact]
    public void ShortPrefixDoesNotPermitAMalformedJoinedWord()
    {
        AssertSuppressed("That looks b", "eutiful in the garden", word =>
        {
            // A rejected join may also probe whether the prefix is a complete word.
            Assert.Contains(word, new[] { "beutiful", "b" });
            return Assessment.CorrectableTypo;
        });
    }

    [Theory]
    [InlineData("(")]
    [InlineData("\"")]
    [InlineData("“")]
    [InlineData("(“")]
    public void ProseOpenersDoNotBypassTheSeamGuard(string opening)
    {
        AssertShownUnchanged("Try " + opening + "b", "uild a spaceship", Knowing("build"));
        AssertSuppressed("Try " + opening + "gre", "atful for this", AllCorrectable);
    }

    [Fact]
    public void UnknownJoinedWordOnlyShowsItsEnding()
    {
        foreach (string prefix in new[] { "C", "Cota" })
        {
            string ending = "Cotabby"[prefix.Length..];
            Assert.Equal(ending, Visible("Use " + prefix, ending + " for everything", KnowsNothing));
        }
    }

    [Fact]
    public void EmptyCompletionShowsNothing()
    {
        AssertSuppressed("Hello", "", KnowsEverything);
    }

    [Fact]
    public void ExtendingAShortDividerIsAllowed()
    {
        AssertShownUnchanged("--", "----", KnowsEverything);
    }
}
