using Cotabby.Core.Input;

namespace Cotabby.Core.Tests.Input;

public sealed class KeyClassifierTests
{
    private const KeyModifiers AltGr = KeyModifiers.Control | KeyModifiers.Alt;
    private const int VkA = 0x41;
    private const int VkQ = 0x51;
    private const int VkSpace = 0x20;
    private const int VkF4 = 0x73;

    private static ClassifiedKey Down(int virtualKey, KeyModifiers held, char translated = '\0') =>
        KeyClassifier.Classify(virtualKey, held, isKeyDown: true, () => translated);

    private static char NeverTranslated() => throw new InvalidOperationException("The key must not be translated.");

    [Theory]
    [InlineData(KeyModifiers.None, 'a')]
    [InlineData(KeyModifiers.Shift, 'A')]
    public void APlainOrShiftedKeyIsTheCharacterTheLayoutGives(KeyModifiers held, char translated)
    {
        Assert.Equal(new(KeyKind.Character, translated, held), Down(VkA, held, translated));
    }

    [Theory]
    [InlineData(AltGr, 'ą')]
    [InlineData(AltGr, '@')]
    [InlineData(AltGr | KeyModifiers.Shift, 'Ą')]
    public void AnAltGrCharacterCountsAsTypingWithoutControlAndAlt(KeyModifiers held, char translated)
    {
        var key = Down(VkQ, held, translated);

        Assert.Equal(new(KeyKind.Character, translated, held & KeyModifiers.Shift), key);
        Assert.False(Event(key, VkQ).HasNonShiftModifier);
    }

    [Theory]
    [InlineData('\0')]
    [InlineData('\u0001')]
    public void CtrlAltWithNoPrintableCharacterIsAShortcut(char translated)
    {
        Assert.Equal(new(KeyKind.Shortcut, '\0', AltGr), Down(VkA, AltGr, translated));
    }

    [Theory]
    [InlineData(KeyModifiers.Control)]
    [InlineData(KeyModifiers.Alt)]
    [InlineData(KeyModifiers.Win)]
    [InlineData(KeyModifiers.Control | KeyModifiers.Shift)]
    [InlineData(AltGr | KeyModifiers.Win)]
    public void AKeyWithCtrlAltOrWinIsAShortcutAndIsNeverTranslated(KeyModifiers held)
    {
        var key = KeyClassifier.Classify(VkA, held, isKeyDown: true, NeverTranslated);

        Assert.Equal(new(KeyKind.Shortcut, '\0', held), key);
    }

    [Fact]
    public void AKeyWithNoCharacterIsOtherAloneAndAShortcutInAChord()
    {
        Assert.Equal(new(KeyKind.Other, '\0', KeyModifiers.None), Down(VkF4, KeyModifiers.None));
        Assert.Equal(new(KeyKind.Other, '\0', KeyModifiers.Shift), Down(VkF4, KeyModifiers.Shift));
        Assert.Equal(new(KeyKind.Shortcut, '\0', KeyModifiers.Alt), Down(VkF4, KeyModifiers.Alt));
    }

    [Theory]
    [InlineData(0x10)]
    [InlineData(0x11)]
    [InlineData(0x12)]
    [InlineData(0x5B)]
    [InlineData(0x5C)]
    [InlineData(0xA0)]
    [InlineData(0xA1)]
    [InlineData(0xA2)]
    [InlineData(0xA3)]
    [InlineData(0xA4)]
    [InlineData(0xA5)]
    public void AModifierOnItsOwnIsOther(int virtualKey)
    {
        foreach (var held in new[] { KeyModifiers.None, KeyModifiers.Control, AltGr, KeyModifiers.Win })
        {
            var key = KeyClassifier.Classify(virtualKey, held, isKeyDown: true, NeverTranslated);

            Assert.Equal(new(KeyKind.Other, '\0', held), key);
        }
    }

    [Theory]
    [InlineData(0x09, KeyKind.Tab)]
    [InlineData(0x1B, KeyKind.Escape)]
    [InlineData(0x0D, KeyKind.Enter)]
    [InlineData(0x08, KeyKind.Backspace)]
    [InlineData(0x2E, KeyKind.Delete)]
    [InlineData(0x21, KeyKind.Arrow)]
    [InlineData(0x22, KeyKind.Arrow)]
    [InlineData(0x23, KeyKind.Arrow)]
    [InlineData(0x24, KeyKind.Arrow)]
    [InlineData(0x25, KeyKind.Arrow)]
    [InlineData(0x26, KeyKind.Arrow)]
    [InlineData(0x27, KeyKind.Arrow)]
    [InlineData(0x28, KeyKind.Arrow)]
    public void NamedKeysKeepTheirKindAndModifiersInAnyChord(int virtualKey, KeyKind kind)
    {
        foreach (var held in new[] { KeyModifiers.None, KeyModifiers.Shift, KeyModifiers.Control, AltGr })
        {
            foreach (bool isKeyDown in new[] { true, false })
            {
                var key = KeyClassifier.Classify(virtualKey, held, isKeyDown, NeverTranslated);

                Assert.Equal(new(kind, '\0', held), key);
            }
        }
    }

    [Fact]
    public void ABoundChordStillMatchesItsBinding()
    {
        var ctrlSpace = new KeyBinding(VkSpace, KeyModifiers.Control, "Ctrl+Space");
        var key = KeyClassifier.Classify(VkSpace, KeyModifiers.Control, isKeyDown: true, NeverTranslated);

        Assert.True(ctrlSpace.Matches(Event(key, VkSpace)));
    }

    [Theory]
    [InlineData(KeyModifiers.None, KeyKind.Other)]
    [InlineData(KeyModifiers.Shift, KeyKind.Other)]
    [InlineData(KeyModifiers.Control, KeyKind.Shortcut)]
    [InlineData(AltGr, KeyKind.Shortcut)]
    public void AKeyUpIsNeverTranslated(KeyModifiers held, KeyKind kind)
    {
        var key = KeyClassifier.Classify(VkA, held, isKeyDown: false, NeverTranslated);

        Assert.Equal(new(kind, '\0', held), key);
    }

    private static KeyboardEvent Event(ClassifiedKey key, int virtualKey) => new()
    {
        Kind = key.Kind,
        Character = key.Character,
        IsKeyDown = true,
        VirtualKey = virtualKey,
        Modifiers = key.Modifiers,
    };
}
