using System.Globalization;
using Cotabby.Core.Macros;

namespace Cotabby.Core.Tests.Macros;

public class MacroReferenceCatalogTests
{
    private static readonly MacroEngine Engine = MacroEngine.Standard(
        now: () => new DateTime(2026, 6, 4, 12, 0, 0),
        culture: CultureInfo.GetCultureInfo("en-US"),
        randomSource: (low, _) => low);

    [Fact]
    public void ListsEveryFamilyInOrder()
    {
        var titles = MacroReferenceCatalog.Build(Engine).Select(group => group.Title);
        Assert.Equal(["Arithmetic", "Unit conversion", "Currency", "Date and time", "Random and generators"], titles);
    }

    [Fact]
    public void ExamplesCarryTheEngineInsertionText()
    {
        var groups = MacroReferenceCatalog.Build(Engine).ToDictionary(group => group.Title, group => group.Examples);

        Assert.Equal(
            [new("5+5=", "10"), new("2^10=", "1024"), new("(3+4)*5=", "35"), new("100*15%=", "15"), new("12/4=", "3")],
            groups["Arithmetic"]);
        Assert.Equal(
            [new("10km->mi", "6.214 mi"), new("100f->c", "37.78 c"), new("5ft->m", "1.524 m"), new("2lb->kg", "0.9072 kg"), new("3cup->ml", "720 ml")],
            groups["Unit conversion"]);
        Assert.Equal(
            [new("100usd->eur", "€92.00"), new("50gbp->jpy", "¥9,557"), new("100 dollars to yen", "¥15,100"), new("$25 to cad", "CA$34.00")],
            groups["Currency"]);
        Assert.Equal(
            [
                new("today", "Jun 4, 2026"), new("now", "12:00 PM"), new("tomorrow", "Jun 5, 2026"),
                new("next-fri", "Jun 5, 2026"), new("+3d", "Jun 7, 2026"), new("+1week", "Jun 11, 2026"),
                new("today(iso)", "2026-06-04"), new("now(24h)", "12:00"),
            ],
            groups["Date and time"]);
        Assert.Equal(6, groups["Random and generators"].Count);
        Assert.Equal(new("d20", "1"), groups["Random and generators"][3]);
    }

    [Fact]
    public void ExamplesTheEngineDeclinesAreDropped()
    {
        var groups = MacroReferenceCatalog.Build(new MacroEngine([new ArithmeticEvaluator()]));

        Assert.Equal(5, groups.Count);
        Assert.Equal(5, groups[0].Examples.Count);
        Assert.All(groups.Skip(1), group => Assert.Empty(group.Examples));
    }

    [Fact]
    public void EveryGroupHasASummary()
    {
        Assert.All(MacroReferenceCatalog.Build(Engine), group => Assert.False(string.IsNullOrWhiteSpace(group.Summary)));
    }
}
