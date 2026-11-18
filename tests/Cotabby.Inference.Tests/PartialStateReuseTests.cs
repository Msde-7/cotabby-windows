namespace Cotabby.Inference.Tests;

public class PartialStateReuseTests
{
    [Theory]
    // A cold prompt saves its checkpoint eight tokens before the end.
    [InlineData(0, 200, 192)]
    // A prompt that adds only a few tokens saves it where evaluation starts.
    [InlineData(195, 200, 195)]
    [InlineData(192, 200, 192)]
    // A prompt shorter than the tail has nothing worth saving.
    [InlineData(0, 5, 0)]
    public void CheckpointPosition_IsNearTheEndButNeverBeforeTheStart(int evaluateFrom, int promptLength, int expected) =>
        Assert.Equal(expected, PartialStateReuse.CheckpointPosition(evaluateFrom, promptLength));

    [Theory]
    // Nothing shared is a cold evaluation.
    [InlineData(0, 200, true, 192, 0, false)]
    // Memory that holds exactly the shared prefix continues from it without a restore.
    [InlineData(200, 200, true, 192, 200, false)]
    [InlineData(150, 150, true, 0, 150, false)]
    // A generation needs the last token's logits, so it rewinds to the checkpoint and replays.
    [InlineData(199, 200, true, 192, 192, true)]
    // Sampled tokens advanced the recurrent state past the prompt, so even the whole prompt rewinds.
    [InlineData(200, 200, false, 192, 192, true)]
    // Backspacing within the tail rewinds, and the checkpoint position itself is enough.
    [InlineData(192, 200, false, 192, 192, true)]
    // An edit before the checkpoint cannot rewind and evaluates fully.
    [InlineData(191, 200, false, 192, 0, false)]
    [InlineData(20, 200, true, 192, 0, false)]
    // Without a checkpoint, advanced memory cannot be reused.
    [InlineData(200, 200, false, 0, 0, false)]
    public void Plan_RewindsOnlyToACheckpointTheSharedPrefixReaches(
        int keep, int remembered, bool memoryExact, int checkpoint, int expectedFrom, bool expectedRestore)
    {
        var (from, restore) = PartialStateReuse.Plan(keep, remembered, memoryExact, checkpoint);
        Assert.Equal(expectedFrom, from);
        Assert.Equal(expectedRestore, restore);
    }

    [Theory]
    [InlineData(0L, false)]
    [InlineData(20_201_924L, true)]
    [InlineData(128L * 1024 * 1024, true)]
    [InlineData(128L * 1024 * 1024 + 1, false)]
    public void FitsCap_KeepsStatesUpTo128MiB(long bytes, bool expected) =>
        Assert.Equal(expected, PartialStateReuse.FitsCap(bytes));
}
