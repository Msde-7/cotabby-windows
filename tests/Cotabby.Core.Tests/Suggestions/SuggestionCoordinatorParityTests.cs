using Cotabby.Core.Input;
using Cotabby.Core.Suggestions;

namespace Cotabby.Core.Tests.Suggestions;

/// <summary>Behaviors ported from upstream in the November 2026 parity pass.</summary>
public sealed class SuggestionCoordinatorParityTests : CoordinatorHarness
{
    private const int JapaneseIme = 0x0411;

    [Fact]
    public async Task Streaming_ShowsFinishedWordsWhileGenerating_ThenTheFinalAnswer()
    {
        Coordinator.Settings = SuggestionSettings.Default with { StreamWhileGenerating = true, PredictAheadWhileTyping = false };
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Engine.Hold = hold;
        Engine.Completion = _ => " world again";

        Type("hello");

        // "again" is still a word in progress as far as the partial knows, so only " world" shows.
        await Eventually(() => Overlay.Text.TrimEnd() == " world", "the finished words of the partial show");
        hold.SetResult();
        await Shown(" world again");
    }

    [Fact]
    public async Task Streaming_AcceptingAPartialFreezesItAndInsertsWhatShows()
    {
        Coordinator.Settings = SuggestionSettings.Default with { StreamWhileGenerating = true, PredictAheadWhileTyping = false };
        Engine.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Engine.Completion = _ => " world again";

        Type("hello");
        await Eventually(() => Overlay.Text.TrimEnd() == " world", "the partial shows");

        Assert.True(Hook.Press(Keys.Tab()));

        await Eventually(() => Focus.Current!.Text.StartsWith("hello world", StringComparison.Ordinal), "the word is inserted");
    }

    [Fact]
    public async Task Streaming_Off_ShowsNothingUntilTheAnswerIsComplete()
    {
        Coordinator.Settings = SuggestionSettings.Default with { PredictAheadWhileTyping = false };
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Engine.Hold = hold;

        Type("hello");

        await StaysHidden();
        hold.SetResult();
        await Shown(" world again");
    }

    [Fact]
    public async Task AnInputMethodComposing_HoldsSuggestionsUntilItCommits()
    {
        Focus.Focus(Fields.Text("") with { ImeLanguageId = JapaneseIme });

        Type("ka");
        await StaysHidden();
        Assert.Empty(Engine.Requests);

        Hook.Press(Keys.Special(KeyKind.Enter));

        await Eventually(() => !Engine.Requests.IsEmpty, "committing the composition asks for a suggestion");
        await Shown(" world again");
    }

    [Fact]
    public async Task AnInputMethodModeKey_CommitsTheComposition()
    {
        Focus.Focus(Fields.Text("") with { ImeLanguageId = JapaneseIme });
        Type("ka");
        await StaysHidden();

        Hook.Press(Keys.Special(KeyKind.Other, 0xF3));

        await Eventually(() => !Engine.Requests.IsEmpty, "switching mode commits and asks for a suggestion");
    }

    [Fact]
    public async Task AnInputMethodTurnedOffWithoutAKey_StopsHoldingSuggestions()
    {
        Focus.Focus(Fields.Text("") with { ImeLanguageId = JapaneseIme });
        Type("ka");
        await StaysHidden();

        // Turned off from the language bar: no key, and no focus event.
        Focus.Replace(Focus.Current! with { ImeLanguageId = 0 });
        Type("n");
        await Task.Delay(100);
        Type("o");

        await Eventually(() => !Engine.Requests.IsEmpty, "typing asks for suggestions again");
    }

    [Fact]
    public async Task AnInputMethodInDirectInput_LeavesSuggestionsAlone()
    {
        Focus.Focus(Fields.Text("") with { ImeLanguageId = 0 });

        Type("hello");

        await Shown(" world again");
    }

    [Fact]
    public async Task MovingToAnotherFieldInTheSameApp_DropsWorkForTheFirstOne()
    {
        Coordinator.Settings = SuggestionSettings.Default with { PredictAheadWhileTyping = false };
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Engine.Hold = hold;
        Focus.Focus(Fields.Text("") with { FieldId = "subject" });
        Type("hello");
        await Eventually(() => !Engine.Requests.IsEmpty, "a request runs");

        Focus.Focus(Fields.Text("hello") with { FieldId = "body" });
        hold.SetResult();

        await StaysHidden();
    }

    [Fact]
    public async Task SelectingText_DropsTheSuggestion()
    {
        Type("hello");
        await Shown(" world again");

        Focus.Replace(Focus.Current! with { HasSelection = true });

        await Shown("");
    }

    [Fact]
    public async Task TextChangingAfterTheCaret_DropsTheSuggestion()
    {
        Type("hello");
        await Shown(" world again");

        Focus.Replace(Focus.Current! with { Text = Focus.Current!.Text + " tail" });

        await Shown("");
    }

    [Fact]
    public async Task ALongWordEnding_HasTheWordsAfterItGeneratedAhead()
    {
        const string prefix = "ask Supercalifr";
        const string word = "Supercalifragilisticexpialidocious";
        Engine.Completion = request => request.Prefix.EndsWith(word, StringComparison.Ordinal)
            ? " is a word"
            : request.Prefix.EndsWith("Supercalifr", StringComparison.Ordinal) ? "agilisticexpialidocious" : "";
        Type(prefix);
        await Shown("agilisticexpialidocious");

        await Eventually(() => Engine.Requests.Any(r => r.Prefix == "ask " + word), "the next words are prefetched");
        Assert.True(Hook.Press(Keys.Tab()));

        await Shown(" is a word");
        Assert.Single(Engine.Requests, r => r.Prefix == "ask " + word);
    }

    [Fact]
    public async Task FocusingAFieldWithText_PrewarmsItsPrompt_AndAKeyCancelsThePrewarm()
    {
        var prewarmed = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Coordinator.Prewarm = async (request, ct) =>
        {
            prewarmed.Enqueue(request.Prefix);
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { cancelled.TrySetResult(); }
        };

        Focus.Focus(Fields.Text("Dear team,") with { FieldId = "body" });
        await Eventually(() => prewarmed.Contains("Dear team,"), "the focused field's prompt is prewarmed");

        Type(" we");
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task FocusingAnEmptyField_PrewarmsNothing()
    {
        int calls = 0;
        Coordinator.Prewarm = (_, _) => { Interlocked.Increment(ref calls); return Task.CompletedTask; };

        Focus.Focus(Fields.Text("") with { FieldId = "empty" });

        await Task.Delay(150);
        Assert.Equal(0, calls);
    }
}
