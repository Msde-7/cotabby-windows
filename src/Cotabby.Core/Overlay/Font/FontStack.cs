namespace Cotabby.Core.Overlay.Font;

/// <summary>
/// Reads the font name a host reports. A web host may report a whole CSS font stack, and the
/// browser draws in its first installed family, so that is the one the ghost should use too.
/// </summary>
public static class FontStack
{
    /// <summary>Families that only exist on macOS. Chromium on Windows skips them.</summary>
    private static readonly HashSet<string> MacOnlyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "-apple-system", "BlinkMacSystemFont", "SF Pro", "SF Pro Text", "SF Pro Display",
        "Helvetica Neue", ".AppleSystemUIFont",
    };

    /// <summary>What Chromium on Windows draws each CSS generic family in.</summary>
    private static readonly Dictionary<string, string> GenericFamilies = new(StringComparer.OrdinalIgnoreCase)
    {
        ["system-ui"] = "Segoe UI",
        ["ui-sans-serif"] = "Segoe UI",
        ["sans-serif"] = "Arial",
        ["serif"] = "Times New Roman",
        ["ui-serif"] = "Times New Roman",
        ["monospace"] = "Consolas",
        ["ui-monospace"] = "Consolas",
        ["cursive"] = "Comic Sans MS",
        ["fantasy"] = "Impact",
    };

    /// <summary>The families named in <paramref name="name"/>, in order, with quotes removed.</summary>
    public static IReadOnlyList<string> Families(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return [];
        var families = new List<string>();
        foreach (var part in name.Split(','))
        {
            var family = part.Trim().Trim('"', '\'').Trim();
            if (family.Length == 0 || MacOnlyNames.Contains(family)) continue;
            families.Add(GenericFamilies.TryGetValue(family, out var mapped) ? mapped : family);
        }
        return families;
    }

    /// <summary>The first family in the stack that can be drawn, or null when none can.</summary>
    public static string? FirstInstalled(string? name, IGlyphMeasurer measurer)
    {
        foreach (var family in Families(name))
        {
            if (measurer.IsInstalled(family)) return family;
        }
        return null;
    }
}
