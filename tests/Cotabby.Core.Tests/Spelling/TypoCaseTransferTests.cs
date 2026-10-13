using Cotabby.Core.Spelling;

namespace Cotabby.Core.Tests.Spelling;

public class TypoCaseTransferTests
{
    [Theory]
    [InlineData("teh", "the", "the")]
    [InlineData("Teh", "the", "The")]
    [InlineData("TEH", "the", "THE")]
    // One uppercase letter is a leading capital, not all caps, so only the first letter is cased.
    [InlineData("Eh", "the", "The")]
    // Interior capitals are treated as noise.
    [InlineData("tEH", "the", "the")]
    // Characters that are not letters are ignored when reading the pattern.
    [InlineData("TEH!", "the", "THE")]
    [InlineData("'Teh", "the", "The")]
    [InlineData("Teh", "", "")]
    [InlineData("123", "the", "the")]
    public void TransfersTheCasePattern(string source, string correction, string expected)
    {
        Assert.Equal(expected, TypoCaseTransfer.Apply(source, correction));
    }

    [Theory]
    // A decomposed capital is one uppercase letter, and the correction's first grapheme is cased as a whole.
    [InlineData("E\u0301cloe", "e\u0301cole", "E\u0301cole")]
    [InlineData("E\u0301CLOE", "école", "ÉCOLE")]
    // A letter outside the basic plane keeps both of its UTF-16 units.
    [InlineData("Teh", "𐐨bc", "𐐀bc")]
    // Swift expands ß to SS when uppercasing.
    [InlineData("STRASE", "straße", "STRASSE")]
    [InlineData("Strase", "straße", "Straße")]
    public void HandlesGraphemesAndFullCaseMappings(string source, string correction, string expected)
    {
        Assert.Equal(expected, TypoCaseTransfer.Apply(source, correction));
    }
}
