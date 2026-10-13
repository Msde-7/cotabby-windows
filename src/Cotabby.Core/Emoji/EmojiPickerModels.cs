namespace Cotabby.Core.Emoji;

/// <summary>
/// One emoji record from the bundled dataset. <see cref="Aliases"/> are the canonical
/// <c>:name:</c> tokens a user types and <see cref="Keywords"/> only widen search recall.
/// </summary>
public sealed record EmojiEntry(
    string Glyph,
    string Name,
    IReadOnlyList<string> Aliases,
    IReadOnlyList<string> Keywords);

/// <summary>
/// A ranked search result. <see cref="Glyph"/> is what the picker shows and inserts. The variant
/// resolver overrides it with a skin toned composition while <see cref="Entry"/> stays the source
/// record used for ranking and usage.
/// </summary>
public sealed record EmojiMatch(EmojiEntry Entry, string Glyph)
{
    public EmojiMatch(EmojiEntry entry) : this(entry, entry.Glyph) { }
}

/// <summary>Skin tone applied to emoji that support Fitzpatrick modifiers.</summary>
public enum EmojiSkinTone
{
    Neutral,
    Light,
    MediumLight,
    Medium,
    MediumDark,
    Dark,
}

/// <summary>Gender preference for emoji that ship neutral, man and woman variants.</summary>
public enum EmojiGender
{
    Neutral,
    Male,
    Female,
}

public static class EmojiVariantExtensions
{
    /// <summary>The Fitzpatrick modifier scalar for a tone, or null for neutral.</summary>
    public static string? Modifier(this EmojiSkinTone tone) => tone switch
    {
        EmojiSkinTone.Light => "\U0001F3FB",
        EmojiSkinTone.MediumLight => "\U0001F3FC",
        EmojiSkinTone.Medium => "\U0001F3FD",
        EmojiSkinTone.MediumDark => "\U0001F3FE",
        EmojiSkinTone.Dark => "\U0001F3FF",
        _ => null,
    };

    public static string DisplayName(this EmojiSkinTone tone) => tone switch
    {
        EmojiSkinTone.Light => "Light",
        EmojiSkinTone.MediumLight => "Medium Light",
        EmojiSkinTone.Medium => "Medium",
        EmojiSkinTone.MediumDark => "Medium Dark",
        EmojiSkinTone.Dark => "Dark",
        _ => "Default",
    };

    /// <summary>
    /// A victory hand in this tone for the settings swatch row. The neutral glyph carries the emoji
    /// variation selector so it does not fall back to the plain text symbol.
    /// </summary>
    public static string SampleGlyph(this EmojiSkinTone tone) =>
        tone.Modifier() is { } modifier ? "✌" + modifier : "✌️";

    public static string DisplayName(this EmojiGender gender) => gender switch
    {
        EmojiGender.Male => "Man",
        EmojiGender.Female => "Woman",
        _ => "Person",
    };

    public static string SampleGlyph(this EmojiGender gender) => gender switch
    {
        EmojiGender.Male => "\U0001F468",
        EmojiGender.Female => "\U0001F469",
        _ => "\U0001F9D1",
    };
}

/// <summary>The emoji customization settings the variant resolver reads at match time.</summary>
public readonly record struct EmojiVariantPreferences(EmojiSkinTone SkinTone, EmojiGender Gender);

/// <summary>Direction for moving the highlighted cell. Up is previous and Down is next.</summary>
public enum EmojiSelectionMove
{
    Up,
    Down,
}

/// <summary>How a capture was committed.</summary>
public enum EmojiCommitMode
{
    /// <summary>A consumed commit key.</summary>
    Key,

    /// <summary>The second colon of <c>:query:</c>.</summary>
    ClosingColon,
}

public enum EmojiTriggerInputKind
{
    Character,
    Backspace,
    Navigate,
    CommitKey,
    Escape,
    DismissExternally,
}

/// <summary>The reduced keystroke vocabulary the trigger state machine understands.</summary>
public readonly record struct EmojiTriggerInput(
    EmojiTriggerInputKind Kind,
    char Character = '\0',
    EmojiSelectionMove Move = default)
{
    public static readonly EmojiTriggerInput Backspace = new(EmojiTriggerInputKind.Backspace);
    public static readonly EmojiTriggerInput CommitKey = new(EmojiTriggerInputKind.CommitKey);
    public static readonly EmojiTriggerInput Escape = new(EmojiTriggerInputKind.Escape);
    public static readonly EmojiTriggerInput DismissExternally = new(EmojiTriggerInputKind.DismissExternally);

    public static EmojiTriggerInput Typed(char character) => new(EmojiTriggerInputKind.Character, character);

    public static EmojiTriggerInput Navigate(EmojiSelectionMove move) =>
        new(EmojiTriggerInputKind.Navigate, Move: move);
}

public enum EmojiTriggerActionKind
{
    Open,
    UpdateQuery,
    MoveSelection,
    Commit,
    Cancel,
}

/// <summary>A side effect the picker session performs after a state machine transition.</summary>
public readonly record struct EmojiTriggerAction(
    EmojiTriggerActionKind Kind,
    string Query = "",
    EmojiSelectionMove Move = default,
    EmojiCommitMode CommitMode = default)
{
    public static readonly EmojiTriggerAction Open = new(EmojiTriggerActionKind.Open);
    public static readonly EmojiTriggerAction Cancel = new(EmojiTriggerActionKind.Cancel);

    public static EmojiTriggerAction UpdateQuery(string query) => new(EmojiTriggerActionKind.UpdateQuery, query);

    public static EmojiTriggerAction MoveSelection(EmojiSelectionMove move) =>
        new(EmojiTriggerActionKind.MoveSelection, Move: move);

    public static EmojiTriggerAction Commit(EmojiCommitMode mode) =>
        new(EmojiTriggerActionKind.Commit, CommitMode: mode);
}
