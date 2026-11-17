namespace Cotabby.Core.Input;

/// <summary>
/// Follows whether an input method is composing text, so Cotabby holds back while the user builds
/// a word the app has not received yet. Upstream reads the composition from the field. Windows
/// gives no other process the composition string, so this follows the keys typed while an input
/// method is converting into its native script, and the keys each language commits with.
/// </summary>
/// <remarks>Touched only on the coordinator's UI context.</remarks>
public sealed class ImeCompositionTracker
{
    private const int LangJapanese = 0x11;
    private const int LangKorean = 0x12;
    private const int LangChinese = 0x04;

    /// <summary>True while keys typed into the input method have not been committed to the field.</summary>
    public bool IsComposing { get; private set; }

    /// <summary>The language of the converting input method, or 0 when none is converting.</summary>
    public int LanguageId { get; private set; }

    /// <summary>Takes the input method state from a fresh read of the focused field.</summary>
    public void Observe(int imeLanguageId)
    {
        LanguageId = imeLanguageId;
        if (imeLanguageId == 0) IsComposing = false;
    }

    /// <summary>Focus moved, so nothing is being composed for the new field yet.</summary>
    public void Reset()
    {
        IsComposing = false;
        LanguageId = 0;
    }

    /// <summary>
    /// Follows one key. Returns true when the key ended a composition, which commits text the app
    /// may want a suggestion for, unless it was Escape, which throws the composition away.
    /// </summary>
    public bool Key(KeyboardEvent ev)
    {
        if (!ev.IsKeyDown || LanguageId == 0) return false;
        bool was = IsComposing;
        switch (ev.Kind)
        {
            case KeyKind.Enter:
            case KeyKind.Escape:
                IsComposing = false;
                return was && ev.Kind == KeyKind.Enter;
            case KeyKind.Character when !ev.HasNonShiftModifier:
                if (was && CommitsComposition(ev.Character))
                {
                    IsComposing = false;
                    return true;
                }
                // Space and punctuation pass straight through an idle input method.
                if (!was && !StartsComposition(ev.Character)) return false;
                IsComposing = true;
                return false;
            default:
                return false;
        }
    }

    /// <summary>A letter typed while converting begins a composition. Space and punctuation do not.</summary>
    private static bool StartsComposition(char c) => char.IsLetterOrDigit(c);

    /// <summary>
    /// Japanese converts with Space and commits only with Enter. Korean commits a syllable at a
    /// space or punctuation. Chinese commits the first candidate with Space, a numbered candidate
    /// with its digit, and punctuation as typed.
    /// </summary>
    private bool CommitsComposition(char c) => (LanguageId & 0x3FF) switch
    {
        LangJapanese => false,
        LangKorean => c == ' ' || char.IsPunctuation(c),
        LangChinese => c == ' ' || char.IsDigit(c) || char.IsPunctuation(c),
        _ => c == ' ',
    };
}
