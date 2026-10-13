namespace Cotabby.Core.Support;

/// <summary>Which end of a section's content to keep when it must be shortened.</summary>
public enum SectionTruncation
{
    PreserveStart,
    PreserveEnd,
}

/// <summary>One budgeted slice of the completion prompt.</summary>
/// <param name="Name">Section identifier.</param>
/// <param name="Content">The text to include.</param>
/// <param name="Priority">Higher priority is filled and kept first when the budget is tight.</param>
/// <param name="MinChars">If the remaining budget cannot fit this many characters, the section is
/// dropped rather than included as a tiny fragment. Zero means "include whatever fits".</param>
/// <param name="MaxChars">Upper bound on the included characters.</param>
/// <param name="Truncation">Which end survives shortening.</param>
/// <param name="PreservesWhitespace">Live editor text must keep its exact caret boundary. Reference
/// notes trim their edges, but opting in keeps spaces and newlines and counts them against the budget.</param>
public sealed record PromptSection(
    string Name,
    string Content,
    int Priority,
    int MinChars,
    int MaxChars,
    SectionTruncation Truncation,
    bool PreservesWhitespace = false);

/// <summary>
/// Budget allocator for the base-model prompt. Each section declares a priority and a character
/// range, and sections are filled highest priority first within a total budget, so the caret text
/// is never starved by a noisy screen capture. Characters are user-perceived characters.
/// </summary>
public static class PromptSectionBudget
{
    /// <summary>
    /// Fills sections by priority (descending, ties broken by original order) within
    /// <paramref name="totalChars"/>. Each section is capped at the smallest of its maximum, its
    /// length and the remaining budget. It is dropped if that is below its minimum or if it becomes
    /// empty. Survivors are returned in their original order.
    /// </summary>
    public static IReadOnlyList<PromptSection> Allocate(IReadOnlyList<PromptSection> sections, int totalChars)
    {
        int remaining = Math.Max(0, totalChars);
        var kept = new PromptSection?[sections.Count];
        foreach (int index in FillOrder(sections))
        {
            if (remaining <= 0) break;
            var section = sections[index];
            var content = new GraphemeString(section.Content);
            int cap = Math.Min(Math.Min(section.MaxChars, content.Count), remaining);
            if (cap < section.MinChars) continue;
            string truncated = AllocatedContent(section, content, cap);
            if (truncated.Length == 0) continue;
            kept[index] = section with { Content = truncated };
            remaining -= GraphemeString.CountOf(truncated);
        }
        return kept.OfType<PromptSection>().ToList();
    }

    /// <summary>
    /// Token-aware variant. The budget is counted in estimated tokens, which respects a model's real
    /// context window more faithfully for code or non-Latin text. Each section's token cap is
    /// converted to a character cap using that content's own characters-per-token density.
    /// </summary>
    public static IReadOnlyList<PromptSection> Allocate(
        IReadOnlyList<PromptSection> sections, int totalTokens, Func<string, int> estimate)
    {
        int remainingTokens = Math.Max(0, totalTokens);
        var kept = new PromptSection?[sections.Count];
        foreach (int index in FillOrder(sections))
        {
            if (remainingTokens <= 0) break;
            var section = sections[index];
            var content = new GraphemeString(section.Content);
            int contentTokens = Math.Max(1, estimate(section.Content));
            double charsPerToken = (double)content.Count / contentTokens;
            int remainingChars = (int)Math.Floor(remainingTokens * charsPerToken);
            int cap = Math.Min(Math.Min(section.MaxChars, content.Count), remainingChars);
            if (cap < section.MinChars) continue;
            string truncated = AllocatedContent(section, content, cap);
            if (truncated.Length == 0) continue;
            kept[index] = section with { Content = truncated };
            // A truncated slice can be token-denser than the section average, so floor at zero.
            // Over-deduction must never read as a hard stop for a section that would still fit.
            remainingTokens = Math.Max(0, remainingTokens - estimate(truncated));
        }
        return kept.OfType<PromptSection>().ToList();
    }

    /// <summary>
    /// Truncates <paramref name="text"/> to at most <paramref name="chars"/> characters, keeping the
    /// start or the end. Returns the input unchanged when it fits and empty when the cap is not positive.
    /// </summary>
    public static string Truncate(string text, int chars, SectionTruncation mode) =>
        Truncate(new GraphemeString(text), chars, mode);

    private static string Truncate(GraphemeString text, int chars, SectionTruncation mode)
    {
        if (chars <= 0) return string.Empty;
        if (text.Count <= chars) return text.Text;
        return mode == SectionTruncation.PreserveStart ? text.Prefix(chars) : text.Suffix(chars);
    }

    private static string AllocatedContent(PromptSection section, GraphemeString content, int characterCap)
    {
        string text = Truncate(content, characterCap, section.Truncation);
        return section.PreservesWhitespace ? text : ScalarSets.TrimWhitespaceAndNewlines(text);
    }

    private static IEnumerable<int> FillOrder(IReadOnlyList<PromptSection> sections) =>
        Enumerable.Range(0, sections.Count).OrderByDescending(index => sections[index].Priority);
}
