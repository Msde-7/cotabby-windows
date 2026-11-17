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
    public void ANoBreakSpaceCountsAsASpace() =>
        Assert.Null(EditRepair.Plan("I think teh ", 4, "the ", "I think the "));
}
