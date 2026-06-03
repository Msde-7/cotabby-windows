using System.Reflection;
using System.Text;
using Cotabby.Inference;
using Xunit;

namespace Cotabby.Core.Tests;

// The degeneration detectors live in Cotabby.Inference but are pure functions
// that need fast feedback. Reflect into them so the unit tests don't require
// an LLM to run.
public class DegenerationGuardSmokeTests
{
    private static readonly MethodInfo HasLongCharRunMethod = typeof(LlamaSuggestionEngine)
        .GetMethod("HasLongCharRun", BindingFlags.NonPublic | BindingFlags.Static)!;
    private static readonly MethodInfo HasRepeatingPatternMethod = typeof(LlamaSuggestionEngine)
        .GetMethod("HasRepeatingPattern", BindingFlags.NonPublic | BindingFlags.Static)!;

    private static bool HasLongCharRun(string s, int threshold = 8) =>
        (bool)HasLongCharRunMethod.Invoke(null, new object[] { new StringBuilder(s), threshold })!;

    private static bool HasRepeatingPattern(string s) =>
        (bool)HasRepeatingPatternMethod.Invoke(null, new object[] { new StringBuilder(s), 40, 3 })!;

    [Fact] public void ShortString_NoCharRun()
        => Assert.False(HasLongCharRun("hello"));

    [Fact] public void LongA_Run_Detected()
        => Assert.True(HasLongCharRun("hello aaaaaaaaaaaa"));

    [Fact] public void WhitespaceRun_NotFlagged()
        => Assert.False(HasLongCharRun("def foo():\n            return"));

    [Fact] public void TwelveSpaces_NotFlagged_PythonIndent()
        => Assert.False(HasLongCharRun("                "));

    [Fact] public void NewlineRun_NotFlagged()
        => Assert.False(HasLongCharRun("\n\n\n\n\n\n\n\n\n\n\n"));

    [Fact] public void NoPattern_ShortText()
        => Assert.False(HasRepeatingPattern("hi my name is gabe"));

    [Fact] public void TwoRepetitions_NotFlagged()
        => Assert.False(HasRepeatingPattern("hi my name is gabe hi my name is gabe"));

    [Fact] public void ThreeRepetitions_Flagged()
        => Assert.True(HasRepeatingPattern("hi my name is gabe hi my name is gabe hi my name is gabe"));

    [Fact] public void ThreeShortRepetitions_Flagged()
        => Assert.True(HasRepeatingPattern("foo foo foo "));

    [Fact] public void LegitimateClosingParens_NotFlagged()
    {
        // Two closing parens should NOT be flagged as a repeating pattern.
        Assert.False(HasRepeatingPattern("def f(g(h(i)))"));
    }
}
