namespace Cotabby.Core.Overlay;

/// <summary>
/// Tells whether the text next to the caret runs right to left. Walks back from the caret, since
/// the closest strong character decides, and falls back to left to right. Mirrors the macOS
/// <c>TextDirectionDetector</c>.
/// </summary>
public static class TextDirectionDetector
{
    public static bool IsRightToLeft(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        for (int i = text.Length - 1; i >= 0; i--)
        {
            char c = text[i];
            if (IsStrongRightToLeft(c)) return true;
            if (IsStrongLeftToRight(c)) return false;
        }
        return false;
    }

    private static bool IsStrongRightToLeft(char c) =>
        // Hebrew, Arabic, Syriac, Thaana, NKo and Arabic Extended.
        c is >= '֐' and <= 'ࣿ'
        // Hebrew and Arabic presentation forms.
        or >= 'יִ' and <= '﷿'
        or >= 'ﹰ' and <= '﻿'
        // Right-to-left and Arabic letter marks.
        or '‏' or '؜';

    private static bool IsStrongLeftToRight(char c) =>
        c is >= 'A' and <= 'Z'
        or >= 'a' and <= 'z'
        // Latin extended, Greek, Cyrillic.
        or >= 'À' and <= 'ɏ'
        or >= 'Ͱ' and <= 'ӿ'
        // CJK ideographs.
        or >= '一' and <= '鿿'
        // Left-to-right mark.
        or '‎';
}
