namespace Cotabby.Inference.Tests;

/// <summary>An overflowing prompt keeps its BOS token and a head that stays put across keystrokes.</summary>
public sealed class PromptWindowTests
{
    private const int Bos = -1;

    private static int[] Prompt(int length, bool bos = true) =>
        bos ? [Bos, .. Enumerable.Range(0, length - 1)] : [.. Enumerable.Range(0, length)];

    [Fact]
    public void APromptThatFitsIsKeptWhole()
    {
        var prompt = Prompt(100);
        Assert.Same(prompt, PromptWindow.Fit(prompt, 100, keepFirst: true));
    }

    [Fact]
    public void AnOverflowingPromptKeepsBosAndFitsTheRoom()
    {
        var kept = PromptWindow.Fit(Prompt(5000), 4000, keepFirst: true);

        Assert.Equal(Bos, kept[0]);
        Assert.True(kept.Length <= 4000);
        // The tail is intact. Only the front was dropped.
        Assert.Equal(Prompt(5000)[^(kept.Length - 1)..], kept[1..]);
        // The cut is a whole number of steps, so it loses less than one step beyond the overflow.
        Assert.True(kept.Length > 4000 - PromptWindow.CutStep);
    }

    [Fact]
    public void TheKeptHeadStaysPutWhileTheUserTypes()
    {
        // Each keystroke adds a token. The kept window must keep sharing its head with the
        // previous one, so only the new tail needs evaluating.
        var previous = PromptWindow.Fit(Prompt(4001), 4000, keepFirst: true);
        int sharedHeads = 0;
        for (int length = 4002; length < 4000 + PromptWindow.CutStep; length++)
        {
            var kept = PromptWindow.Fit(Prompt(length), 4000, keepFirst: true);
            Assert.Equal(previous, kept[..previous.Length]);
            previous = kept;
            sharedHeads++;
        }
        Assert.Equal(PromptWindow.CutStep - 2, sharedHeads);
    }

    [Fact]
    public void TheHeadMovesByAWholeStepOnceThePromptOutgrowsIt()
    {
        var before = PromptWindow.Fit(Prompt(4000 + PromptWindow.CutStep), 4000, keepFirst: true);
        var after = PromptWindow.Fit(Prompt(4001 + PromptWindow.CutStep), 4000, keepFirst: true);

        Assert.Equal(before[1] + PromptWindow.CutStep, after[1]);
    }

    [Fact]
    public void WithoutBosOnlyTheTailIsKept()
    {
        var kept = PromptWindow.Fit(Prompt(5000, bos: false), 4000, keepFirst: false);

        Assert.True(kept.Length <= 4000);
        Assert.Equal(4999, kept[^1]);
        Assert.Equal(0, kept[0] % PromptWindow.CutStep);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(10)]
    public void ATinyWindowStillKeepsTheLastToken(int maxTokens)
    {
        var kept = PromptWindow.Fit(Prompt(50), maxTokens, keepFirst: true);

        Assert.InRange(kept.Length, 1, maxTokens);
        Assert.Equal(48, kept[^1]);
        if (maxTokens > 1) Assert.Equal(Bos, kept[0]);
    }

    [Fact]
    public void NoRoomKeepsNothing()
    {
        Assert.Empty(PromptWindow.Fit(Prompt(10), 0, keepFirst: true));
    }
}
