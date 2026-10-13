using Cotabby.Core.Focus;
using Cotabby.Core.Input;

namespace Cotabby.Core.Emoji;

/// <summary>The floating panel the picker session drives.</summary>
public interface IEmojiPickerPanel
{
    void Show(string query, IReadOnlyList<EmojiMatch> matches, int selectedIndex, ScreenRect caretRect);

    void SetSelectedIndex(int index);

    void Hide();
}

/// <summary>
/// Replaces the typed query run in <paramref name="field"/> with <paramref name="glyph"/>.
/// <paramref name="fallbackDeleteCount"/> is the typed run length, for hosts whose text cannot be measured.
/// </summary>
public delegate void EmojiQueryReplacer(FocusedField field, string glyph, int fallbackDeleteCount);

/// <summary>
/// Orchestrates one inline <c>:emoji:</c> picker. Feeds keystrokes to the trigger state machine,
/// ranks matches with the user's usage and variant preferences, drives the panel and hands the
/// committed glyph to the replacer. Not thread safe, so the host serializes all calls.
/// </summary>
public sealed class EmojiPickerSession(
    Func<EmojiMatcher> matcherProvider,
    IEmojiPickerPanel panel,
    IFocusTracker focus,
    EmojiUsageStore usage,
    EmojiQueryReplacer replaceQuery)
{
    private readonly EmojiTriggerStateMachine _machine = new();

    // Built on first capture. Decoding the bundled catalog is wasted on sessions that never open the picker.
    private EmojiMatcher? _matcher;
    private string _query = "";
    private IReadOnlyList<EmojiMatch> _matches = [];
    private int _selectedIndex;
    private FocusedField? _captureField;

    public bool Enabled { get; set; } = true;

    public EmojiSkinTone PreferredSkinTone { get; set; }

    public EmojiGender PreferredGender { get; set; }

    /// <summary>Whether a capture may open in a field. The host supplies the app-wide rules.</summary>
    public Func<FocusedField, bool> CanCapture { get; set; } = _ => true;

    public bool IsCapturing => _machine.IsCapturing;

    /// <summary>Feeds one keyboard event. Returns whether the key must be kept from the focused app.</summary>
    public bool HandleKey(KeyboardEvent keyEvent) =>
        Enabled ? Apply(_machine.Reduce(keyEvent, _matches.Count)) : StandDown();

    /// <summary>Feeds one reduced input. Returns whether its key must be kept from the focused app.</summary>
    public bool Handle(EmojiTriggerInput input) =>
        Enabled ? Apply(_machine.Reduce(input, _matches.Count)) : StandDown();

    /// <summary>
    /// Follows the caret while the capture's field keeps focus and cancels when focus moves to
    /// another field or a secure one.
    /// </summary>
    public void HandleFocusChanged(FocusedField? field)
    {
        if (!IsCapturing) return;
        if (field is null || field.IsSecure || !IsCaptureField(field))
        {
            Cancel();
            return;
        }
        _captureField = field;
        PresentPanel();
    }

    /// <summary>Ends any open capture without inserting anything.</summary>
    public void Cancel()
    {
        if (IsCapturing) TeardownCapture();
    }

    private bool StandDown()
    {
        Cancel();
        return false;
    }

    private bool Apply(EmojiTriggerStateMachine.Output output)
    {
        if (output.Action is not { } action) return output.ConsumesKey;

        switch (action.Kind)
        {
            case EmojiTriggerActionKind.Open:
                BeginCapture();
                break;
            case EmojiTriggerActionKind.UpdateQuery:
                UpdateQuery(action.Query);
                break;
            case EmojiTriggerActionKind.MoveSelection:
                MoveSelection(action.Move);
                break;
            case EmojiTriggerActionKind.Commit when action.CommitMode == EmojiCommitMode.ClosingColon:
                return CommitClosingColon();
            case EmojiTriggerActionKind.Commit:
                Commit(_matches[_selectedIndex]);
                break;
            case EmojiTriggerActionKind.Cancel:
                TeardownCapture();
                break;
        }
        return output.ConsumesKey;
    }

    private void BeginCapture()
    {
        if (focus.Current is not { IsSecure: false } field || !CanCapture(field))
        {
            _machine.Reset();
            return;
        }
        _captureField = field;
        // A bare colon immediately shows the user's recents, padded with popular emoji.
        RefreshMatches("");
        PresentPanel();
    }

    private void UpdateQuery(string query)
    {
        RefreshMatches(query);
        PresentPanel();
    }

    /// <summary>
    /// Moves the panel to a freshly read caret. Focus events do not fire for caret movement, and
    /// reading the field is too slow for the keyboard hook, so the host reads it and reports back.
    /// </summary>
    public void UpdateCaret(FocusedField field)
    {
        if (!IsCapturing || field.IsSecure) return;
        _captureField = field;
        PresentPanel();
    }

    private void MoveSelection(EmojiSelectionMove move)
    {
        int step = move == EmojiSelectionMove.Up ? -1 : 1;
        _selectedIndex = (_selectedIndex + step + _matches.Count) % _matches.Count;
        panel.SetSelectedIndex(_selectedIndex);
    }

    /// <summary>
    /// The closing colon of <c>:query:</c>. With a match the colon is swallowed, so the field holds
    /// <c>:query</c> exactly as it does for a commit key. Without one it passes through and the
    /// literal text stays untouched.
    /// </summary>
    private bool CommitClosingColon()
    {
        // An empty query shows recents, which a bare "::" must not commit.
        var alias = _query.ToLowerInvariant();
        var match = alias.Length == 0
            ? null
            : _matches.FirstOrDefault(candidate => candidate.Entry.Aliases.Contains(alias)) ?? _matches.FirstOrDefault();
        if (match is null)
        {
            TeardownCapture();
            return false;
        }
        Commit(match);
        return true;
    }

    private void Commit(EmojiMatch match)
    {
        var field = _captureField!;
        int typedLength = _query.Length + 1;
        // Usage is keyed by the base primary alias, which is stable across variants.
        if (match.Entry.Aliases.Count > 0) usage.Record(match.Entry.Aliases[0]);
        TeardownCapture();
        replaceQuery(field, match.Glyph, typedLength);
    }

    private void TeardownCapture()
    {
        _machine.Reset();
        _matches = [];
        _selectedIndex = 0;
        _query = "";
        _captureField = null;
        panel.Hide();
    }

    private void RefreshMatches(string query)
    {
        _matcher ??= matcherProvider();
        _query = query;
        var snapshot = usage.Snapshot();
        var ranked = query.Length == 0 ? _matcher.Recents(snapshot) : _matcher.Matches(query, snapshot);
        _matches = EmojiVariantResolver.Resolve(ranked, new EmojiVariantPreferences(PreferredSkinTone, PreferredGender));
        _selectedIndex = 0;
    }

    private void PresentPanel() => panel.Show(_query, _matches, _selectedIndex, _captureField!.CaretRect);

    // UI Automation offers no stable element identity here, so the process and field bounds stand in.
    private bool IsCaptureField(FocusedField field) =>
        field.ProcessId == _captureField!.ProcessId && field.FieldRect == _captureField.FieldRect;
}
