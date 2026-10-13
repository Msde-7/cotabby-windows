namespace Cotabby.Core.Macros;

/// <summary>One reference row, a query (without the <c>/</c>) and the text it inserts.</summary>
public sealed record MacroReferenceExample(string Input, string Result);

/// <summary>A macro family with a short description and a few worked examples.</summary>
public sealed record MacroReferenceGroup(string Title, string Summary, IReadOnlyList<MacroReferenceExample> Examples);

/// <summary>
/// Reference listing of every macro family for a settings pane. Examples run through the
/// real engine so the listing cannot drift from actual behavior. An example the engine
/// declines is dropped rather than shown with a stale result.
/// </summary>
public static class MacroReferenceCatalog
{
    private static readonly (string Title, string Summary, string[] Inputs)[] Families =
    [
        (
            "Arithmetic",
            "Safe arithmetic: + - * / ^, parentheses, decimals, and trailing % (percent). "
                + "Accepting inserts only the result.",
            ["5+5=", "2^10=", "(3+4)*5=", "100*15%=", "12/4="]
        ),
        (
            "Unit conversion",
            "Same-quantity conversions across length, mass, temperature, and volume. Fully offline.",
            ["10km->mi", "100f->c", "5ft->m", "2lb->kg", "3cup->ml"]
        ),
        (
            "Currency",
            "Offline currency conversions using a bundled approximate rate table. "
                + "Accepts ISO codes, symbols, and common names.",
            ["100usd->eur", "50gbp->jpy", "100 dollars to yen", "$25 to cad"]
        ),
        (
            "Date and time",
            "Locale-aware dates and times. Supports weekday navigation, relative offsets, "
                + "and format hints like (iso), (long), (short), (24h).",
            ["today", "now", "tomorrow", "next-fri", "+3d", "+1week", "today(iso)", "now(24h)"]
        ),
        (
            "Random and generators",
            "Random numbers, dice (/d20), coin flips, and UUIDs.",
            ["random", "random(100)", "random(1,6)", "d20", "coin", "uuid"]
        ),
    ];

    public static IReadOnlyList<MacroReferenceGroup> Build(MacroEngine engine) =>
    [
        .. Families.Select(family => new MacroReferenceGroup(
            family.Title,
            family.Summary,
            [
                .. family.Inputs
                    .Select(input => (Input: input, Result: engine.Evaluate(input)))
                    .Where(example => example.Result is not null)
                    .Select(example => new MacroReferenceExample(example.Input, example.Result!.InsertionText)),
            ])),
    ];
}
