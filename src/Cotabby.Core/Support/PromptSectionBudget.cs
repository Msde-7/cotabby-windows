namespace Cotabby.Core.Support;

/// <summary>How a section is trimmed when it exceeds its budget.</summary>
public enum SectionTruncation
{
    /// <summary>Keep the first N chars (context sections).</summary>
    PreserveStart,

    /// <summary>Keep the last N chars (the caret prefix — text nearest the caret wins).</summary>
    PreserveEnd,
}

/// <summary>One budgeted slice of the completion prompt. Mirrors the macOS
/// port's <c>PromptSection</c>.</summary>
public sealed record PromptSection(
    string Name,
    string Content,
    int Priority,
    int MinChars,
    int MaxChars,
    SectionTruncation Truncation);

/// <summary>
/// Character-budget allocator for prompt sections. Mirrors the macOS
/// <c>PromptSectionBudget</c>: fill by priority descending (ties keep
/// original order), cap each section at min(maxChars, contentLength,
/// remaining budget), drop a section whose cap falls below its minChars or
/// whose trimmed content is empty, and return survivors in original order.
/// </summary>
public static class PromptSectionBudget
{
    public static IReadOnlyList<PromptSection> Allocate(
        IReadOnlyList<PromptSection> sections, int totalChars)
    {
        var byPriority = sections
            .Select((s, i) => (Section: s, Index: i))
            .OrderByDescending(t => t.Section.Priority)
            .ThenBy(t => t.Index)
            .ToList();

        int remaining = Math.Max(0, totalChars);
        var kept = new Dictionary<int, PromptSection>();

        foreach (var (section, index) in byPriority)
        {
            int cap = Math.Min(Math.Min(section.MaxChars, section.Content.Length), remaining);
            if (cap < section.MinChars) continue;

            string content = section.Content.Length <= cap
                ? section.Content
                : section.Truncation == SectionTruncation.PreserveEnd
                    ? section.Content[^cap..]
                    : section.Content[..cap];

            content = content.Trim();
            if (content.Length == 0) continue;

            kept[index] = section with { Content = content };
            remaining -= content.Length;
        }

        return kept.OrderBy(kv => kv.Key).Select(kv => kv.Value).ToList();
    }
}
