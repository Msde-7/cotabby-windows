using Cotabby.Core.Insertion;

namespace Cotabby.Core.Tests.Insertion;

public sealed class EditRepairTests
{
    [Fact]
    public void AnEditThatLandedNeedsNothing() =>
        Assert.Null(EditRepair.Plan("The total is /12*3", 5, "36", "The total is 36"));

    [Fact]
    public void LostTextIsTypedAgain() =>
        Assert.Equal(new EditRepairPlan(0, "36"), EditRepair.Plan("The total is /12*3", 5, "36", "The total is "));

    [Fact]
    public void AnInsertThatNeverArrivedIsTypedAgain() =>
        Assert.Equal(new EditRepairPlan(0, " world"), EditRepair.Plan("hello", 0, " world", "hello"));

    [Fact]
    public void APartlyTypedInsertIsFinished() =>
        Assert.Equal(new EditRepairPlan(0, "ld again"), EditRepair.Plan("hello", 0, " world again", "hello wor"));

    [Fact]
    public void ALostBackspaceIsSentAndTheTextRetyped() =>
        Assert.Equal(new EditRepairPlan(3, "36"), EditRepair.Plan("The total is /12*3", 5, "36", "The total is /36"));

    [Fact]
    public void LostBackspacesAndLostTextAreBothRepaired() =>
        Assert.Equal(new EditRepairPlan(2, "the "), EditRepair.Plan("I think teh ", 4, "the ", "I think te"));

    [Fact]
    public void AnUnclearOutcomeIsLeftAlone() =>
        Assert.Null(EditRepair.Plan("The total is /12*3", 5, "36", "Something else entirely"));

    [Fact]
    public void TheUserTypingAfterTheEditIsNotUndone() =>
        Assert.Null(EditRepair.Plan("hello", 0, " world", "hello world and more"));

    [Fact]
    public void AnEmojiTakesOneBackspaceToTakeBack() =>
        // The thumbs-up landed after a lost backspace. Taking it back is one key, not two.
        Assert.Equal(
            new EditRepairPlan(8, "world \U0001F44D"),
            EditRepair.Plan("hello wor", 3, "world \U0001F44D", "hello wworld \U0001F44D"));

    [Fact]
    public void LostBackspacesAreCountedInKeysNotUnits() =>
        // Two of the three keys that should have removed "a", the emoji and "b" were lost, and the text never came.
        Assert.Equal(new EditRepairPlan(2, "x"), EditRepair.Plan("za\U0001F600b", 4, "x", "za\U0001F600"));

    [Fact]
    public void ADeletedLineBreakIsMeasuredBeforeItIsDropped() =>
        Assert.Null(EditRepair.Plan("one teh\r\n", 5, "the", "one the"));

    [Theory]
    [InlineData("teh", 3)]
    [InlineData("\U0001F44D", 1)]
    [InlineData("\U0001F44D\U0001F3FD", 1)] // a skin tone joins the emoji
    [InlineData("\U0001F469‍\U0001F4BB", 1)] // a joined sequence
    [InlineData("\U0001F1FA\U0001F1F8", 1)] // a flag
    [InlineData("\r\n", 1)]
    [InlineData("é", 2)] // a combining accent is its own key
    [InlineData("कि", 2)] // and so is a Devanagari vowel sign
    [InlineData("a\U0001D400b", 3)] // a letter outside the basic plane is one key
    public void BackspacesFollowWhatHostsDelete(string text, int expected) =>
        Assert.Equal(expected, EditRepair.BackspaceCount(text));

    [Fact]
    public void BackspacesForADeleteCountReadTheTextBeforeTheCaret()
    {
        Assert.Equal(2, EditRepair.BackspacesFor("go \U0001F44D!", 3));
        // Units past the known text count one key each.
        Assert.Equal(5, EditRepair.BackspacesFor("abc", 5));
    }

    [Fact]
    public void ANoBreakSpaceCountsAsASpace() =>
        Assert.Null(EditRepair.Plan("I think teh ", 4, "the ", "I think the "));
}
