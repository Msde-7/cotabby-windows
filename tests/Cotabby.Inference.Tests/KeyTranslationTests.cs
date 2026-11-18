using Cotabby.Win32.Input;

namespace Cotabby.Inference.Tests;

public class KeyTranslationTests
{
    [Fact]
    public void ALetterAfterADeadKey_IsUnknown()
    {
        // US-International: ' then e shows é in the host, which only a fresh read of the field can tell.
        bool afterDeadKey = false;
        Assert.Equal('\0', KeyTranslation.FromLayout(-1, '\'', ref afterDeadKey));
        Assert.Equal(KeyTranslation.Unknown, KeyTranslation.FromLayout(1, 'e', ref afterDeadKey));
        Assert.Equal('e', KeyTranslation.FromLayout(1, 'e', ref afterDeadKey));
    }

    [Fact]
    public void AKeyWithNoCharacter_KeepsTheDeadKeyPending()
    {
        bool afterDeadKey = false;
        KeyTranslation.FromLayout(-1, '`', ref afterDeadKey);
        Assert.Equal('\0', KeyTranslation.FromLayout(0, '\0', ref afterDeadKey));
        Assert.Equal(KeyTranslation.Unknown, KeyTranslation.FromLayout(1, 'a', ref afterDeadKey));
    }

    [Theory]
    [InlineData(0x08)] // Backspace
    [InlineData(0x09)] // Tab
    [InlineData(0x0D)] // Enter
    [InlineData(0x1B)] // Escape
    public void AKeyThatMakesAControlCharacter_UsesUpTheDeadKey(int virtualKey)
    {
        bool afterDeadKey = false;
        KeyTranslation.FromLayout(-1, '\'', ref afterDeadKey);
        KeyTranslation.Untranslated(virtualKey, ref afterDeadKey);
        Assert.Equal('e', KeyTranslation.FromLayout(1, 'e', ref afterDeadKey));
    }

    [Theory]
    [InlineData(0x09)] // Tab swallowed as the accept key
    [InlineData(0x1B)] // Escape captured by the emoji picker
    [InlineData(0x0D)] // Enter captured by a macro
    public void AControlKeyAHandlerSwallowed_KeepsTheDeadKeyPending(int virtualKey)
    {
        bool afterDeadKey = false;
        KeyTranslation.FromLayout(-1, '\'', ref afterDeadKey);
        KeyTranslation.Handled(virtualKey, suppressed: true, ref afterDeadKey);
        Assert.Equal(KeyTranslation.Unknown, KeyTranslation.FromLayout(1, 'e', ref afterDeadKey));
    }

    [Fact]
    public void AControlKeyThatReachedTheHost_UsesUpTheDeadKey()
    {
        bool afterDeadKey = false;
        KeyTranslation.FromLayout(-1, '\'', ref afterDeadKey);
        KeyTranslation.Handled(0x09, suppressed: false, ref afterDeadKey);
        Assert.Equal('e', KeyTranslation.FromLayout(1, 'e', ref afterDeadKey));
    }

    [Theory]
    [InlineData(0x25)] // Left arrow
    [InlineData(0x2E)] // Delete
    [InlineData(0x10)] // Shift
    public void AKeyThatMakesNoCharacter_KeepsTheDeadKeyPending(int virtualKey)
    {
        bool afterDeadKey = false;
        KeyTranslation.FromLayout(-1, '\'', ref afterDeadKey);
        KeyTranslation.Untranslated(virtualKey, ref afterDeadKey);
        Assert.Equal(KeyTranslation.Unknown, KeyTranslation.FromLayout(1, 'e', ref afterDeadKey));
    }

    [Fact]
    public void PlainKeys_ReportTheirCharacter()
    {
        bool afterDeadKey = false;
        Assert.Equal('x', KeyTranslation.FromLayout(1, 'x', ref afterDeadKey));
        Assert.False(afterDeadKey);
    }

    [Fact]
    public void ASurrogatePairFromThePacketKey_IsOneUnknownCharacter()
    {
        Assert.Equal('\0', KeyTranslation.FromPacket('\uD83D'));
        Assert.Equal(KeyTranslation.Unknown, KeyTranslation.FromPacket('\uDE00'));
        Assert.Equal('a', KeyTranslation.FromPacket('a'));
    }
}
