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
