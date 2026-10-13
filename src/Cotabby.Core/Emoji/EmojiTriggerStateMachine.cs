using Cotabby.Core.Input;

namespace Cotabby.Core.Emoji;

/// <summary>
/// The trigger state machine for the inline <c>:emoji:</c> picker. It owns only the capture
/// lifecycle and the live query. Selection and matches live in <see cref="EmojiPickerSession"/>,
/// which tells the machine the current match count.
/// </summary>
/// <remarks>
/// Query characters are never consumed. They flow into the focused field and the commit replaces
/// the literal run there. Navigation, commit and Escape are consumed so the picker can be driven
/// without the foreground app seeing those keys.
/// </remarks>
public sealed class EmojiTriggerStateMachine
{
    private const char Trigger = ':';

    // The last character typed while idle. Null means nothing is known about the text before the caret.
    private char? _previousCharacter;

    /// <summary>The side effect of one input and whether its key is swallowed before the focused app sees it.</summary>
    public readonly record struct Output(EmojiTriggerAction? Action, bool ConsumesKey)
    {
        public static readonly Output Ignored = default;
    }

    public bool IsCapturing { get; private set; }

    /// <summary>The query typed after the trigger colon. Empty when idle.</summary>
    public string Query { get; private set; } = "";

    /// <summary>Forces the machine back to idle with no memory of the preceding character.</summary>
    public void Reset() => GoIdle(null);

    /// <summary>
    /// Feeds one keyboard event. Key releases and bare modifier or function keys are ignored.
    /// </summary>
    public Output Reduce(KeyboardEvent keyEvent, int selectableMatchCount) =>
        TriggerInput(keyEvent) is { } input ? Reduce(input, selectableMatchCount) : Output.Ignored;

    /// <summary><paramref name="selectableMatchCount"/> is the number of matches currently displayed.</summary>
    public Output Reduce(EmojiTriggerInput input, int selectableMatchCount) =>
        IsCapturing ? ReduceCapturing(input, selectableMatchCount) : ReduceIdle(input);

    private const int VkLeft = 0x25, VkUp = 0x26, VkRight = 0x27, VkDown = 0x28;

    private static EmojiTriggerInput? TriggerInput(KeyboardEvent keyEvent)
    {
        if (!keyEvent.IsKeyDown) return null;
        if (keyEvent.HasNonShiftModifier) return EmojiTriggerInput.DismissExternally;

        return keyEvent.Kind switch
        {
            KeyKind.Character => EmojiTriggerInput.Typed(keyEvent.Character),
            KeyKind.Tab => EmojiTriggerInput.CommitKey,
            KeyKind.Escape => EmojiTriggerInput.Escape,
            KeyKind.Backspace => EmojiTriggerInput.Backspace,
            KeyKind.Arrow when keyEvent.VirtualKey is VkLeft or VkUp => EmojiTriggerInput.Navigate(EmojiSelectionMove.Up),
            KeyKind.Arrow when keyEvent.VirtualKey is VkRight or VkDown => EmojiTriggerInput.Navigate(EmojiSelectionMove.Down),
            KeyKind.Enter or KeyKind.Delete or KeyKind.Arrow => EmojiTriggerInput.DismissExternally,
            _ => null,
        };
    }

    private Output ReduceIdle(EmojiTriggerInput input)
    {
        if (input.Kind != EmojiTriggerInputKind.Character)
        {
            // Any other activity erases what is known about the text before the caret.
            _previousCharacter = null;
            return Output.Ignored;
        }
        if (input.Character == Trigger && IsTriggerBoundary(_previousCharacter))
        {
            IsCapturing = true;
            Query = "";
            return new Output(EmojiTriggerAction.Open, false);
        }
        _previousCharacter = input.Character;
        return Output.Ignored;
    }

    private Output ReduceCapturing(EmojiTriggerInput input, int selectableMatchCount)
    {
        switch (input.Kind)
        {
            case EmojiTriggerInputKind.Character:
                return ReduceCapturingCharacter(input.Character);

            case EmojiTriggerInputKind.Backspace:
                if (Query.Length == 0)
                {
                    // This backspace deletes the trigger colon itself.
                    return Cancel(consumesKey: false);
                }
                Query = Query[..^1];
                return new Output(EmojiTriggerAction.UpdateQuery(Query), false);

            case EmojiTriggerInputKind.Navigate when selectableMatchCount > 0:
                return new Output(EmojiTriggerAction.MoveSelection(input.Move), true);

            case EmojiTriggerInputKind.CommitKey when selectableMatchCount > 0:
                GoIdle(null);
                return new Output(EmojiTriggerAction.Commit(EmojiCommitMode.Key), true);

            case EmojiTriggerInputKind.Escape:
                return Cancel(consumesKey: true);

            default:
                // Navigation or commit with nothing to act on passes through to the focused app.
                return Cancel(consumesKey: false);
        }
    }

    private Output ReduceCapturingCharacter(char character)
    {
        if (character == Trigger)
        {
            GoIdle(null);
            return new Output(EmojiTriggerAction.Commit(EmojiCommitMode.ClosingColon), false);
        }
        if (IsNameCharacter(character))
        {
            Query += character;
            return new Output(EmojiTriggerAction.UpdateQuery(Query), false);
        }
        // Whitespace or punctuation ends the capture. The terminator is remembered so a following
        // colon is judged against it.
        GoIdle(character);
        return new Output(EmojiTriggerAction.Cancel, false);
    }

    private Output Cancel(bool consumesKey)
    {
        GoIdle(null);
        return new Output(EmojiTriggerAction.Cancel, consumesKey);
    }

    private void GoIdle(char? previousCharacter)
    {
        IsCapturing = false;
        Query = "";
        _previousCharacter = previousCharacter;
    }

    // A capture only begins at the start of the field or after whitespace, which keeps "http://",
    // "12:30" and "foo::bar" from opening the picker.
    private static bool IsTriggerBoundary(char? previous) => previous is not { } character || char.IsWhiteSpace(character);

    // The punctuation mirrors gemoji aliases such as "+1" and "-1".
    private static bool IsNameCharacter(char character) =>
        char.IsLetter(character) || char.IsNumber(character) || character is '_' or '+' or '-';
}
