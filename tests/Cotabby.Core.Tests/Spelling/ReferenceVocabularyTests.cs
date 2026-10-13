using Cotabby.Core.Spelling;

namespace Cotabby.Core.Tests.Spelling;

public class ReferenceVocabularyTests
{
    [Fact]
    public void TheWordBeingTypedIsNotAReference()
    {
        var words = ReferenceVocabulary.Words("Cotabby helps. Use Cota", trailingText: "", glossary: "");
        Assert.Contains("Cotabby", words);
        Assert.DoesNotContain("Cota", words);
    }

    [Fact]
    public void WordsComeFromAllThreeSourcesAndKeepConnectors()
    {
        var words = ReferenceVocabulary.Words("It's a well-known -fact- (résumé) x2 par", "tail text", "Glossary");
        Assert.Equal(
            new HashSet<string> { "It's", "a", "well-known", "résumé", "x", "tail", "text", "Glossary" },
            words);
    }
}
