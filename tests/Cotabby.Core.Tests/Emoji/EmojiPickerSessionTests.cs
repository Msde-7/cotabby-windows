using Cotabby.Core.Emoji;
using Cotabby.Core.Focus;
using Cotabby.Core.Input;

namespace Cotabby.Core.Tests.Emoji;

public sealed class EmojiPickerSessionTests : IDisposable
{
    private const string Smile = "😄";
    private const string Smiley = "😃";
    private const string Wave = "\U0001F44B";

    private static readonly ScreenRect FieldRect = new(0, 0, 400, 30);

    private readonly string _usagePath = Path.Combine(Path.GetTempPath(), $"cotabby-emoji-session-{Guid.NewGuid():N}.json");
    private readonly FakeFocus _focus = new() { Field = Field() };
    private readonly FakePanel _panel = new();
    private readonly List<(FocusedField Field, string Glyph, int FallbackDeleteCount)> _replacements = [];
    private readonly EmojiUsageStore _usage;
    private readonly EmojiPickerSession _sut;

    public EmojiPickerSessionTests()
    {
        // ":smile" ranks the exact alias first and the "smiley" alias prefix second, so navigation
        // has somewhere to go.
        var catalog = new EmojiCatalog(
        [
            new EmojiEntry(Smile, "smiling face", ["smile"], []),
            new EmojiEntry(Smiley, "grinning face with big eyes", ["smiley"], []),
            new EmojiEntry(Wave, "waving hand", ["wave"], []),
        ]);
        _usage = new EmojiUsageStore(_usagePath);
        _sut = new EmojiPickerSession(
            () => new EmojiMatcher(catalog),
            _panel,
            _focus,
            _usage,
            (field, glyph, fallbackDeleteCount) => _replacements.Add((field, glyph, fallbackDeleteCount)));
    }

    public void Dispose() => File.Delete(_usagePath);

    private static FocusedField Field(bool secure = false, int processId = 1, double caretX = 10) => new()
    {
        ElementHandle = new object(),
        ProcessId = processId,
        ProcessName = "test",
        Text = "",
        CaretOffset = 0,
        CaretRect = new ScreenRect(caretX, 10, 2, 18),
        FieldRect = FieldRect,
        IsSingleLine = false,
        IsSecure = secure,
    };

    private static KeyboardEvent Key(KeyKind kind, char character = '\0', bool modifier = false) =>
        new() { Kind = kind, Character = character, IsKeyDown = true, HasNonShiftModifier = modifier };

    private bool Type(string text)
    {
        bool consumed = false;
        foreach (char character in text) consumed = _sut.HandleKey(Key(KeyKind.Character, character));
        return consumed;
    }

    private string[] ShownGlyphs => _panel.Matches.Select(match => match.Glyph).ToArray();

    // Commit

    [Fact]
    public void CommitKeyIsConsumedAndReplacesTheQueryWithTheSelectedGlyph()
    {
        Type(":smile");

        Assert.True(_sut.HandleKey(Key(KeyKind.Tab)));

        Assert.Equal((_focus.Field!, Smile, 6), Assert.Single(_replacements));
        Assert.False(_panel.IsVisible);
        Assert.False(_sut.IsCapturing);
        Assert.Equal(["smile"], _usage.Snapshot().RecentAliases);
    }

    [Fact]
    public void ClosingColonWithAMatchIsSwallowedAndReplacesTheQuery()
    {
        Type(":smile");

        // The colon never reaches the field, so the run to delete is ":smile" as for a commit key.
        Assert.True(Type(":"));

        Assert.Equal((_focus.Field!, Smile, 6), Assert.Single(_replacements));
        Assert.Equal(["smile"], _usage.Snapshot().RecentAliases);
        Assert.False(_sut.IsCapturing);
    }

    [Fact]
    public void ClosingColonCommitsTheTopMatchOfAPartialQuery()
    {
        Assert.True(Type(":smil:"));

        Assert.Equal((_focus.Field!, Smile, 5), Assert.Single(_replacements));
    }

    [Fact]
    public void ClosingColonWithNoMatchLeavesLiteralTextUntouched()
    {
        Assert.False(Type(":zzzzz:"));

        Assert.Empty(_replacements);
        Assert.Empty(_usage.Snapshot().RecentAliases);
        Assert.False(_panel.IsVisible);
    }

    [Fact]
    public void BareDoubleColonDoesNotCommitTheShownRecents()
    {
        Type(":");
        Assert.NotEmpty(_panel.Matches);

        Assert.False(Type(":"));

        Assert.Empty(_replacements);
        Assert.False(_sut.IsCapturing);
    }

    // Keys that pass through

    [Fact]
    public void EnterDismissesAndPassesThroughEvenWithMatches()
    {
        Type(":smile");

        Assert.False(_sut.HandleKey(Key(KeyKind.Enter)));

        Assert.Empty(_replacements);
        Assert.False(_panel.IsVisible);
    }

    [Fact]
    public void CommitKeyWithNoMatchesPassesThroughWithoutInserting()
    {
        Type(":zzzzz");

        // Tab is never stolen when there is nothing to insert, so a suggestion accept still works.
        Assert.False(_sut.HandleKey(Key(KeyKind.Tab)));

        Assert.Empty(_replacements);
        Assert.False(_sut.IsCapturing);
    }

    [Fact]
    public void EscapeIsConsumedAndCancelsWithoutInserting()
    {
        Type(":smile");

        Assert.True(_sut.HandleKey(Key(KeyKind.Escape)));

        Assert.Empty(_replacements);
        Assert.False(_panel.IsVisible);
        Assert.False(_sut.IsCapturing);
    }

    [Fact]
    public void ModifiedKeyDismissesCapture()
    {
        Type(":smile");

        Assert.False(_sut.HandleKey(Key(KeyKind.Tab, modifier: true)));

        Assert.False(_sut.IsCapturing);
        Assert.Empty(_replacements);
    }

    [Fact]
    public void BackspaceShortensQueryButModifiedBackspaceDismisses()
    {
        Type(":smile");

        _sut.HandleKey(Key(KeyKind.Backspace));
        Assert.True(_sut.IsCapturing);
        Assert.Equal("smil", _panel.Query);

        _sut.HandleKey(Key(KeyKind.Backspace, modifier: true));
        Assert.False(_sut.IsCapturing);
        Assert.False(_panel.IsVisible);
    }

    [Fact]
    public void OrdinaryTypingIsNotInvolved()
    {
        Assert.False(Type("hello"));

        Assert.False(_sut.IsCapturing);
        Assert.Equal(0, _panel.ShowCount);
        Assert.Equal(0, _focus.RefreshCount);
    }

    // Navigation

    [Theory]
    [InlineData(EmojiSelectionMove.Up)]
    [InlineData(EmojiSelectionMove.Down)]
    public void NavigationIsConsumedAndKeepsPanelOpenWhenMatchesPresent(EmojiSelectionMove move)
    {
        Type(":smile");

        Assert.True(_sut.Handle(EmojiTriggerInput.Navigate(move)));
        Assert.True(_panel.IsVisible);
    }

    [Fact]
    public void SelectionWrapsInBothDirectionsAndCommitUsesIt()
    {
        Type(":smile");
        Assert.Equal([Smile, Smiley], ShownGlyphs);

        // Previous from the first cell wraps to the last and next from the last wraps to the first.
        _sut.Handle(EmojiTriggerInput.Navigate(EmojiSelectionMove.Up));
        _sut.Handle(EmojiTriggerInput.Navigate(EmojiSelectionMove.Down));
        _sut.Handle(EmojiTriggerInput.Navigate(EmojiSelectionMove.Up));
        Assert.Equal([1, 0, 1], _panel.SelectedIndexUpdates);

        _sut.HandleKey(Key(KeyKind.Tab));

        Assert.Equal(Smiley, Assert.Single(_replacements).Glyph);
        Assert.Equal(["smiley"], _usage.Snapshot().RecentAliases);
    }

    [Fact]
    public void NavigationWithNoMatchesPassesThroughAndClosesCapture()
    {
        Type(":zzzzz");

        Assert.False(_sut.Handle(EmojiTriggerInput.Navigate(EmojiSelectionMove.Down)));
        Assert.False(_panel.IsVisible);
    }

    // Capture lifecycle and gating

    [Fact]
    public void BareColonOpensPanelImmediatelyWithRecentsThenPopularEmoji()
    {
        _usage.Record("wave");

        Assert.False(Type(":"));

        Assert.True(_sut.IsCapturing);
        Assert.True(_panel.IsVisible);
        Assert.Equal("", _panel.Query);
        Assert.Equal(0, _panel.SelectedIndex);
        Assert.Equal(_focus.Field!.CaretRect, _panel.CaretRect);
        Assert.Equal([Wave, Smile, Smiley], ShownGlyphs);
    }

    [Fact]
    public void TypedQueryRanksWithUsageAndResetsTheSelection()
    {
        Type(":smil");
        _sut.Handle(EmojiTriggerInput.Navigate(EmojiSelectionMove.Down));
        Assert.Equal([Smile, Smiley], ShownGlyphs);

        _usage.Record("smiley");
        _sut.HandleKey(Key(KeyKind.Backspace));

        Assert.Equal("smi", _panel.Query);
        Assert.Equal([Smiley, Smile], ShownGlyphs);
        Assert.Equal(0, _panel.SelectedIndex);
    }

    [Fact]
    public void VariantPreferencesAreAppliedToShownAndCommittedGlyphs()
    {
        _sut.PreferredSkinTone = EmojiSkinTone.Medium;
        Type(":wave");
        Assert.Equal([Wave + "\U0001F3FD", Wave], ShownGlyphs);

        _sut.HandleKey(Key(KeyKind.Tab));

        Assert.Equal(Wave + "\U0001F3FD", Assert.Single(_replacements).Glyph);
        // Usage is keyed by the base alias so it is stable across variants.
        Assert.Equal(["wave"], _usage.Snapshot().RecentAliases);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SecureOrUnsupportedFieldAbortsOpenWithoutShowingPanel(bool secure)
    {
        _focus.Field = secure ? Field(secure: true) : null;

        Assert.False(Type(":"));

        Assert.False(_sut.IsCapturing);
        Assert.Equal(0, _panel.ShowCount);
    }

    [Fact]
    public void DisablingMidCaptureCancelsAndStandsDown()
    {
        Type(":smi");

        _sut.Enabled = false;

        Assert.False(_sut.HandleKey(Key(KeyKind.Tab)));
        Assert.False(_sut.IsCapturing);
        Assert.False(_panel.IsVisible);
        Assert.Empty(_replacements);
    }

    [Fact]
    public void CaretUpdateRepositionsThePanel()
    {
        Type(":smi");
        var moved = Field(caretX: 90);

        _sut.UpdateCaret(moved);

        Assert.Equal(moved.CaretRect, _panel.CaretRect);
    }

    [Fact]
    public void FocusEventForSameFieldRepositionsPanelAtNewCaret()
    {
        Type(":smi");
        var moved = Field(caretX: 90);

        _sut.HandleFocusChanged(moved);

        Assert.True(_sut.IsCapturing);
        Assert.Equal(moved.CaretRect, _panel.CaretRect);
        Assert.Equal("smi", _panel.Query);
    }

    [Fact]
    public void FocusChangeOrSecureFieldDuringCaptureCancels()
    {
        FocusedField?[] changes = [Field(processId: 2), Field(secure: true), null];
        foreach (var change in changes)
        {
            Type(":smi");

            _sut.HandleFocusChanged(change);

            Assert.False(_sut.IsCapturing);
            Assert.False(_panel.IsVisible);
        }
    }

    [Fact]
    public void FocusEventWhileIdleDoesNothing()
    {
        _sut.HandleFocusChanged(Field());

        Assert.Equal(0, _panel.ShowCount);
        Assert.Equal(0, _panel.HideCount);
    }

    [Fact]
    public void CancelClosesAnOpenCaptureAndIsQuietWhenIdle()
    {
        Type(":smi");

        _sut.Cancel();
        _sut.Cancel();

        Assert.False(_sut.IsCapturing);
        Assert.False(_panel.IsVisible);
        Assert.Equal(1, _panel.HideCount);
    }

    private sealed class FakeFocus : IFocusTracker
    {
        public FocusedField? Field { get; set; }

        public int RefreshCount { get; private set; }

        public FocusedField? Current => Field;

        public event EventHandler<FocusedField?>? FocusChanged { add { } remove { } }

        public FocusedField? Refresh()
        {
            RefreshCount++;
            return Field;
        }

        public void Start() { }

        public void Stop() { }

        public void Dispose() { }
    }

    private sealed class FakePanel : IEmojiPickerPanel
    {
        public bool IsVisible { get; private set; }
        public int ShowCount { get; private set; }
        public int HideCount { get; private set; }
        public string Query { get; private set; } = "";
        public IReadOnlyList<EmojiMatch> Matches { get; private set; } = [];
        public int SelectedIndex { get; private set; }
        public ScreenRect CaretRect { get; private set; }
        public List<int> SelectedIndexUpdates { get; } = [];

        public void Show(string query, IReadOnlyList<EmojiMatch> matches, int selectedIndex, ScreenRect caretRect)
        {
            IsVisible = true;
            ShowCount++;
            Query = query;
            Matches = matches;
            SelectedIndex = selectedIndex;
            CaretRect = caretRect;
        }

        public void SetSelectedIndex(int index)
        {
            SelectedIndex = index;
            SelectedIndexUpdates.Add(index);
        }

        public void Hide()
        {
            IsVisible = false;
            HideCount++;
        }
    }
}
