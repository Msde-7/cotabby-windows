using System.Text;

namespace Cotabby.Core.Emoji;

/// <summary>
/// Turns raw matcher results into the variant aware cells the picker shows. Gender is resolved
/// first, collapsing the neutral, man and woman siblings of one concept, then the skin tone is
/// composed onto emoji that support it. The bundled catalog carries no variant metadata, so the
/// policy lives here.
/// </summary>
public static class EmojiVariantResolver
{
    private const string ManPrefix = "man_";
    private const string WomanPrefix = "woman_";

    public static IReadOnlyList<EmojiMatch> Resolve(
        IReadOnlyList<EmojiMatch> matches,
        EmojiVariantPreferences preferences) =>
        ApplySkinTone(ApplyGender(matches, preferences.Gender), preferences.SkinTone);

    /// <summary>
    /// Collapses the variants of one concept (<c>firefighter</c>, <c>man_firefighter</c>,
    /// <c>woman_firefighter</c>) to the preferred gender, falling back to neutral and then to the
    /// highest ranked variant present. A concept with a single variant passes through.
    /// </summary>
    private static List<EmojiMatch> ApplyGender(IReadOnlyList<EmojiMatch> matches, EmojiGender gender)
    {
        var families = new List<List<(EmojiGender Gender, EmojiMatch Match)>>();
        var familyByConcept = new Dictionary<string, List<(EmojiGender Gender, EmojiMatch Match)>>();

        foreach (var match in matches)
        {
            var (variantGender, concept) = GenderVariant(match);
            if (!familyByConcept.TryGetValue(concept, out var family))
            {
                family = [];
                familyByConcept[concept] = family;
                families.Add(family);
            }
            // Only the highest ranked match of each gender is kept.
            if (family.All(variant => variant.Gender != variantGender)) family.Add((variantGender, match));
        }

        return families
            .Select(family => family
                .OrderBy(variant => variant.Gender == gender ? 0 : variant.Gender == EmojiGender.Neutral ? 1 : 2)
                .First().Match)
            .ToList();
    }

    // Classifies by the dataset's alias convention, where "man_firefighter" is the male "firefighter".
    private static (EmojiGender Gender, string Concept) GenderVariant(EmojiMatch match)
    {
        var alias = match.Entry.Aliases.Count > 0 ? match.Entry.Aliases[0] : match.Entry.Name;
        if (alias.StartsWith(ManPrefix, StringComparison.Ordinal)) return (EmojiGender.Male, alias[ManPrefix.Length..]);
        if (alias.StartsWith(WomanPrefix, StringComparison.Ordinal)) return (EmojiGender.Female, alias[WomanPrefix.Length..]);
        return (EmojiGender.Neutral, alias);
    }

    // A toned cell is followed by its default glyph so the unmodified variant stays selectable.
    private static IReadOnlyList<EmojiMatch> ApplySkinTone(List<EmojiMatch> matches, EmojiSkinTone skinTone)
    {
        if (skinTone.Modifier() is not { } modifier) return matches;

        var result = new List<EmojiMatch>();
        foreach (var match in matches)
        {
            if (TonedGlyph(match.Glyph, modifier) is { } toned) result.Add(match with { Glyph = toned });
            result.Add(match);
        }
        return result;
    }

    /// <summary>
    /// Inserts the Fitzpatrick <paramref name="modifier"/> after the leading scalar when that scalar
    /// is a modifier base. Covers single scalar emoji and ZWJ sequences that start with a person.
    /// Returns null when the emoji does not support skin tone or already carries a modifier.
    /// </summary>
    public static string? TonedGlyph(string glyph, string modifier)
    {
        var scalars = glyph.EnumerateRunes().ToList();
        if (scalars.Count == 0 || !IsModifierBase(scalars[0])) return null;
        if (scalars.Count > 1 && scalars[1].Value is >= 0x1F3FB and <= 0x1F3FF) return null;

        return string.Concat(scalars[0].ToString(), modifier, glyph.AsSpan(scalars[0].Utf16SequenceLength));
    }

    /// <summary>
    /// The Unicode <c>Emoji_Modifier_Base</c> set. Composing a modifier onto any other emoji renders
    /// as a broken glyph pair, so an emoji missing from the set stays untoned.
    /// </summary>
    public static bool IsModifierBase(Rune scalar) => scalar.Value is
        0x261D or 0x26F9 or (>= 0x270A and <= 0x270D) or 0x1F385
        or (>= 0x1F3C2 and <= 0x1F3C4) or 0x1F3C7 or (>= 0x1F3CA and <= 0x1F3CC)
        or (>= 0x1F442 and <= 0x1F443) or (>= 0x1F446 and <= 0x1F450) or (>= 0x1F466 and <= 0x1F478) or 0x1F47C
        or (>= 0x1F481 and <= 0x1F483) or (>= 0x1F485 and <= 0x1F487) or 0x1F48F or 0x1F491 or 0x1F4AA
        or (>= 0x1F574 and <= 0x1F575) or 0x1F57A or 0x1F590 or (>= 0x1F595 and <= 0x1F596)
        or (>= 0x1F645 and <= 0x1F647) or (>= 0x1F64B and <= 0x1F64F) or 0x1F6A3 or (>= 0x1F6B4 and <= 0x1F6B6)
        or 0x1F6C0 or 0x1F6CC or 0x1F90C or 0x1F90F or (>= 0x1F918 and <= 0x1F91F) or 0x1F926
        or (>= 0x1F930 and <= 0x1F939) or (>= 0x1F93C and <= 0x1F93E) or 0x1F977 or (>= 0x1F9B5 and <= 0x1F9B6)
        or (>= 0x1F9B8 and <= 0x1F9B9) or 0x1F9BB or (>= 0x1F9CD and <= 0x1F9CF) or (>= 0x1F9D1 and <= 0x1F9DD)
        or (>= 0x1FAC3 and <= 0x1FAC5) or (>= 0x1FAF0 and <= 0x1FAF8);
}
