using Cotabby.Core.Support;
using Assessment = Cotabby.Core.Support.CompletionSeamGuard.SpellingAssessment;
using Decision = Cotabby.Core.Support.CompletionSeamGuard.PresentationDecision;
using Streamed = Cotabby.Core.Support.CompletionSeamGuard.StreamedLeadingWordVerdict;
using Verdict = Cotabby.Core.Support.CompletionSeamGuard.Verdict;

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

    private static Verdict Final(string precedingText, string completion, Func<string, Assessment> spelling) =>
        CompletionSeamGuard.FinalVerdict(precedingText, completion, spelling);

    private static Decision Present(
        string precedingText, string completion, bool isFinal, Func<string, Assessment> spelling) =>
        CompletionSeamGuard.Presentation(precedingText, completion, isFinal, spelling);

    private static Streamed Stream(string precedingText, string completion, Func<string, Assessment> spelling) =>
        CompletionSeamGuard.StreamedLeadingWord(precedingText, completion, spelling);

    private static readonly bool[] BothPhases = [false, true];

    [Fact]
    public void FreshPunctuationRunIsSuppressed()
    {
        Assert.Equal(Verdict.JunkPunctuationRun, Final("Wait", " what....", KnowsEverything));
    }

    [Fact]
    public void SymbolRunIsSuppressed()
    {
        Assert.Equal(Verdict.JunkPunctuationRun, Final("Price: ", "$$$$", KnowsEverything));
    }

    [Fact]
    public void ThreeCharacterRunIsAllowed()
    {
        Assert.Equal(Verdict.Allow, Final("Well", "... maybe", KnowsEverything));
    }

    [Fact]
    public void SingleTrailingCharacterDoesNotExemptAJunkRun()
    {
        // Only a real preceding run of two or more reads as a divider being extended.
        Assert.Equal(Verdict.JunkPunctuationRun, Final("Hello.", "....", KnowsEverything));
    }

    [Fact]
    public void StreamedPresentationRejectsJunkRuns()
    {
        Assert.Equal(
            Decision.Suppress(Verdict.JunkPunctuationRun),
            Present("Wait", " what....", isFinal: false, KnowsEverything));
    }

    [Fact]
    public void ContinuingAnExistingDividerIsAllowed()
    {
        Assert.Equal(Verdict.Allow, Final("----", "------", KnowsEverything));
    }

    [Fact]
    public void FreshDividerAwayFromSeamIsSuppressed()
    {
        Assert.Equal(Verdict.JunkPunctuationRun, Final("----", " section ======", KnowsEverything));
    }

    [Fact]
    public void RepeatedLettersAreNotJunk()
    {
        Assert.Equal(Verdict.Allow, Final("That is so", " coooool", KnowsEverything));
    }

    [Fact]
    public void MissingSeparatorBetweenKnownWordsIsRepairedForFinalAndStreamedText()
    {
        foreach (bool isFinal in BothPhases)
        {
            foreach (string prefix in new[] { "up", "delay" })
            {
                Assert.Equal(
                    Decision.Show(" and it takes time", wordOnly: false),
                    Present(
                        "predictions come " + prefix, "and it takes time", isFinal,
                        KnownElseCorrectable(prefix, "and")));
            }
        }
    }

    [Fact]
    public void SeparatorRepairWaitsForCompleteFirstWord()
    {
        Assert.Equal(Decision.Wait, Present("come up", "an", isFinal: false, KnownElseCorrectable("up", "an")));
    }

    [Fact]
    public void SeparatorRepairPreservesValidJoinsAndRejectsUnknownPieces()
    {
        Assert.Equal(
            Decision.Show("pet on the floor", wordOnly: false),
            Present("a car", "pet on the floor", isFinal: true, Knowing("car", "pet", "carpet")));
        foreach (string known in new[] { "gre", "atful" })
        {
            Assert.Equal(
                Decision.Suppress(Verdict.SeamMisspelling("greatful")),
                Present("gre", "atful for this", isFinal: true, KnownElseCorrectable(known)));
        }
        // Unknown joins must not be rewritten.
        Assert.Equal(
            Decision.Show("bar", wordOnly: true),
            Present("foo", "bar next", isFinal: true, Knowing("foo", "bar")));
    }

    [Fact]
    public void SeparatorRepairDoesNotSplitContractions()
    {
        Assert.Equal(
            Decision.Suppress(Verdict.SeamMisspelling("don't")),
            Present("don", "'t go", isFinal: true, Correctable("don't")));
        // A trailing apostrophe is still the contraction being typed, not a finished word.
        Assert.Equal(
            Decision.Suppress(Verdict.SeamMisspelling("don't")),
            Present("they said don'", "t go", isFinal: true, KnownElseCorrectable("don'", "t")));
    }

    [Fact]
    public void SeparatorRepairTreatsWholeContractionsAsWords()
    {
        Assert.Equal(
            Decision.Show(" we've decided to move forward", wordOnly: false),
            Present(
                "let you know that", "we've decided to move forward", isFinal: true,
                KnownElseCorrectable("that", "we've")));
        Assert.Equal(
            Decision.Show(" going to be fine", wordOnly: false),
            Present("I think it’s", "going to be fine", isFinal: true, KnownElseCorrectable("it’s", "going")));
        // Streaming still waits until the contraction is complete and followed by a boundary.
        foreach (string partial in new[] { "we'", "we've" })
        {
            Assert.Equal(
                Decision.Wait,
                Present("know that", partial, isFinal: false, KnownElseCorrectable("that", "we've")));
        }
    }

    [Fact]
    public void MisspelledSeamWordIsSuppressed()
    {
        Assert.Equal(Verdict.SeamMisspelling("greatful"), Final("I am so gre", "atful for this", AllCorrectable));
    }

    [Fact]
    public void KnownSeamWordIsAllowed()
    {
        Assert.Equal(Verdict.Allow, Final("I am so gre", "at to hear it", Knowing("great")));
    }

    [Fact]
    public void SeamRuleOnlyAppliesMidWord()
    {
        Assert.Equal(Verdict.Allow, Final("I am so ", "greatful", KnowsNothing));
    }

    [Fact]
    public void CapitalizedSeamWordIsAllowed()
    {
        // Names and brands are routinely out of dictionary.
        Assert.Equal(Verdict.Allow, Final("Ask Cota", "bby about it", KnowsNothing));
    }

    [Fact]
    public void ShortSeamWordIsAllowed()
    {
        Assert.Equal(Verdict.Allow, Final("a", "t the office", KnowsNothing));
    }

    [Fact]
    public void DigitAdjacentSeamIsAllowed()
    {
        Assert.Equal(Verdict.Allow, Final("version 2", "024 release", KnowsNothing));
    }

    [Fact]
    public void CjkSeamIsAllowed()
    {
        Assert.Equal(Verdict.Allow, Final("これはとても良", "い天気ですね", KnowsNothing));
    }

    [Fact]
    public void OrdinaryContinuationIsAllowed()
    {
        Assert.Equal(
            Verdict.Allow,
            Final("Thanks again for your help", " with the move last weekend.", Knowing("with")));
    }

    [Fact]
    public void MisspelledLeadingWordWithCorrectionIsSuppressed()
    {
        Assert.Equal(
            Verdict.LeadingWordMisspelling("ecrir"), Final("Je veux ", "ecrir plus vite", Correctable("ecrir")));
    }

    [Fact]
    public void LeadingWordWithoutCorrectionIsAllowed()
    {
        // An unknown name or domain term stays visible when the checker has no suggestion for it.
        Assert.Equal(
            Verdict.Allow,
            Final("Use ", "cotabby avec soin", word => word == "cotabby" ? Assessment.UncorrectableTypo : Assessment.Known));
    }

    [Fact]
    public void CapitalizedLeadingWordIsAllowed()
    {
        Assert.Equal(
            Verdict.Allow,
            Final("Ask ", "Cotypist about it", MustNotBeCalled("capitalized leading words bypass spelling")));
    }

    [Fact]
    public void MidWordCompletionOnlyAssessesTheJoinedSeamWord()
    {
        Assert.Equal(Verdict.Allow, Final("Je veux ecr", "irregular", word =>
        {
            Assert.Equal("ecrirregular", word);
            return Assessment.Known;
        }));
    }

    [Fact]
    public void QuotedLeadingWordIsSuppressed()
    {
        Assert.Equal(
            Verdict.LeadingWordMisspelling("ecrir"),
            Final("Il répond ", "“ecrir” plus vite", Correctable("ecrir")));
    }

    [Fact]
    public void ParenthesizedLeadingWordAfterTextIsSuppressed()
    {
        Assert.Equal(
            Verdict.LeadingWordMisspelling("ecrir"),
            Final("Il répond", ": (ecrir) plus vite", Correctable("ecrir")));
    }

    [Fact]
    public void ContractionIsAssessedAsOneWord()
    {
        Assert.Equal(Verdict.Allow, Final("It ", "doesn't matter", word =>
        {
            Assert.Equal("doesn't", word);
            return Assessment.Known;
        }));
    }

    [Fact]
    public void LetterAndDigitLeadingTokenIsAllowedWithoutSpellLookup()
    {
        Assert.Equal(
            Verdict.Allow,
            Final("Use ", "ecrir2 here", MustNotBeCalled("letter-and-digit tokens bypass spelling")));
    }

    [Fact]
    public void FinalVerdictSuppressesCorrectableLeadingWordWithoutTrailingBoundary()
    {
        Assert.Equal(Verdict.LeadingWordMisspelling("ecrir"), Final("Je veux ", "ecrir", Correctable("ecrir")));
    }

    [Fact]
    public void ConnectorContinuationOfTheCaretWordSkipsTheLeadingWordRule()
    {
        Assert.Equal(Verdict.Allow, Final("I don", "'t know", word =>
        {
            Assert.Equal("don't", word);
            return Assessment.Known;
        }));
    }

    [Fact]
    public void HyphenatedLeadingWordIsAssessedAsOneToken()
    {
        Assert.Equal(Verdict.Allow, Final("A ", "state-of-the-art tool", word =>
        {
            Assert.Equal("state-of-the-art", word);
            return Assessment.Known;
        }));
    }

    [Fact]
    public void ShortLeadingWordIsAllowedWithoutSpellLookup()
    {
        // Words under four letters are too ambiguous to judge, so even "teh" passes.
        Assert.Equal(
            Verdict.Allow, Final("Send ", "teh report", MustNotBeCalled("short leading words bypass spelling")));
    }

    [Fact]
    public void StreamedLeadingWordWaitsUntilItsBoundaryArrives()
    {
        Assert.Equal(
            Streamed.Wait,
            Stream("Je veux ", "ecrir", MustNotBeCalled("an incomplete streamed word is not assessed")));
    }

    [Fact]
    public void StreamedContractionWaitsAfterADanglingApostrophe()
    {
        Assert.Equal(
            Streamed.Wait,
            Stream("It ", "does'", MustNotBeCalled("a dangling apostrophe may still continue the word")));
    }

    [Fact]
    public void StreamedCorrectableLeadingWordIsSuppressedAtItsBoundary()
    {
        Assert.Equal(Streamed.Suppress, Stream("Je veux ", "ecrir ", Correctable("ecrir")));
    }

    [Fact]
    public void StreamedKnownLeadingWordIsAllowedAtItsBoundary()
    {
        Assert.Equal(Streamed.Allow, Stream("Je veux ", "écrire ", KnownElseCorrectable("écrire")));
    }

    [Fact]
    public void StreamedLetterAndDigitLeadingTokenIsAllowedWithoutSpellLookup()
    {
        Assert.Equal(
            Streamed.Allow,
            Stream("Use ", "ecrir2 ", MustNotBeCalled("letter-and-digit tokens bypass streamed spelling")));
    }

    [Fact]
    public void AbandonedFragmentIsSuppressedEvenIfTheDictionaryRecognizesIt()
    {
        foreach (bool isFinal in BothPhases)
        {
            Assert.Equal(
                Decision.Suppress(Verdict.AbandonedWord("roo")),
                Present("book a roo", " room for two guests", isFinal, KnowsEverything));
        }
        Assert.Equal(
            Decision.Show(" is parked", wordOnly: false),
            Present("a car", " is parked", isFinal: true, KnowsEverything));
    }

    [Fact]
    public void BadJoinedWordNeverAppearsDuringStreaming()
    {
        Assert.Equal(
            Decision.Wait, Present("so gre", "atf", isFinal: false, MustNotBeCalled("wait for the full word")));
        foreach (bool isFinal in BothPhases)
        {
            Assert.Equal(
                Decision.Suppress(Verdict.SeamMisspelling("greatful")),
                Present("so gre", "atful for this", isFinal, AllCorrectable));
        }
    }

    [Fact]
    public void KnownJoinedWordKeepsItsPhraseAtEveryPrefixLength()
    {
        const string word = "build";
        for (int prefixLength = 1; prefixLength < word.Length; prefixLength++)
        {
            string completion = word[prefixLength..] + " a spaceship";
            foreach (bool isFinal in BothPhases)
            {
                Assert.Equal(
                    Decision.Show(completion, wordOnly: false),
                    Present("I would like to " + word[..prefixLength], completion, isFinal, Knowing(word)));
            }
        }
    }

    [Fact]
    public void KnownJoinedWordStillWaitsForItsStreamedBoundary()
    {
        // A dictionary entry is not a word boundary, because "build" could still grow into "building".
        foreach (string completion in new[] { "u", "uil", "uild", "uilding" })
        {
            Assert.Equal(
                Decision.Wait,
                Present(
                    "I would like to b", completion, isFinal: false,
                    MustNotBeCalled("an unfinished joined word is not assessed")));
        }
        Assert.Equal(
            Decision.Show("uild a spaceship", wordOnly: false),
            Present("I would like to b", "uild a spaceship", isFinal: false, Knowing("build")));
    }

    [Fact]
    public void ShortPrefixDoesNotPermitAMalformedJoinedWord()
    {
        foreach (bool isFinal in BothPhases)
        {
            Assert.Equal(
                Decision.Suppress(Verdict.SeamMisspelling("beutiful")),
                Present("That looks b", "eutiful in the garden", isFinal, word =>
                {
                    // A rejected join may also probe whether the prefix is a complete word.
                    Assert.Contains(word, new[] { "beutiful", "b" });
                    return Assessment.CorrectableTypo;
                }));
        }
    }

    [Theory]
    [InlineData("(")]
    [InlineData("\"")]
    [InlineData("“")]
    [InlineData("(“")]
    public void ProseOpenersDoNotBypassTheStreamedSeamGuard(string opening)
    {
        Assert.Equal(Decision.Wait, Present("Try " + opening + "b", "uild", isFinal: false, KnowsEverything));
        foreach (bool isFinal in BothPhases)
        {
            Assert.Equal(
                Decision.Show("uild a spaceship", wordOnly: false),
                Present("Try " + opening + "b", "uild a spaceship", isFinal, Knowing("build")));
            Assert.Equal(
                Decision.Suppress(Verdict.SeamMisspelling("greatful")),
                Present("Try " + opening + "gre", "atful for this", isFinal, AllCorrectable));
        }
    }

    [Fact]
    public void UnknownJoinedWordOnlyShowsItsEnding()
    {
        foreach (bool isFinal in BothPhases)
        {
            foreach (string prefix in new[] { "C", "Cota" })
            {
                string ending = "Cotabby"[prefix.Length..];
                Assert.Equal(
                    Decision.Show(ending, wordOnly: true),
                    Present("Use " + prefix, ending + " for everything", isFinal, KnowsNothing));
            }
        }
    }

    [Fact]
    public void EmptyCompletionWaitsAndFinalVerdictAllows()
    {
        Assert.Equal(Decision.Wait, Present("Hello", "", isFinal: false, KnowsEverything));
        Assert.Equal(Streamed.Wait, Stream("Hello", "", KnowsEverything));
        Assert.Equal(Verdict.Allow, Final("Hello", "", KnowsEverything));
    }

    [Fact]
    public void RepeatedDigitsAreNotJunk()
    {
        Assert.Equal(Verdict.Allow, Final("PIN hint: ", "0000 then 1111", KnowsEverything));
    }

    [Fact]
    public void ExtendingAShortDividerIsAllowedOnlyOnceFinal()
    {
        // A pure divider has no word to assess, so streaming keeps buffering it.
        Assert.Equal(Decision.Show("----", wordOnly: false), Present("--", "----", isFinal: true, KnowsEverything));
        Assert.Equal(Decision.Wait, Present("--", "----", isFinal: false, KnowsEverything));
    }

    [Fact]
    public void AbandonedFragmentDependsOnWhetherItIsACorrectableTypo()
    {
        Assert.Equal(
            Decision.Suppress(Verdict.AbandonedWord("cta")),
            Present("the cta", " is here", isFinal: true, Correctable("cta")));
        // An uncorrectable fragment (a name, jargon) may legitimately be finished by the writer.
        Assert.Equal(
            Decision.Show(" is here", wordOnly: false),
            Present(
                "the cta", " is here", isFinal: true,
                word => word == "cta" ? Assessment.UncorrectableTypo : Assessment.Known));
    }

    [Fact]
    public void CamelCaseLeadingTokenBypassesSpelling()
    {
        Assert.Equal(
            Verdict.Allow,
            Final("Set ", "myVaraible here", MustNotBeCalled("camelCase identifiers bypass spelling")));
    }
}
