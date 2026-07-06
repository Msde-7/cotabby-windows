using System.Text;

namespace Cotabby.Core.Support;

/// <summary>
/// Renders the prompt for the base-model completion pipeline. Direct port of
/// the macOS <c>BaseCompletionPromptRenderer</c>.
///
/// Design: a *base* model has no instruction-following channel and will
/// happily continue a bare "Task:" line as if it were the document, so an
/// instruction-blob prompt would leak scaffolding into the ghost text. This
/// renderer treats the model as a pure text continuer: persona, style, and
/// language are folded into a short conditioning preface (a base model
/// conditions on description, it does not obey commands), and the caret
/// prefix is the LAST thing in the prompt with trailing whitespace trimmed so
/// generation begins at a clean word boundary.
/// </summary>
public static class BaseCompletionPromptRenderer
{
    /// <summary>Total character budget for the preface plus caret prefix.</summary>
    public const int DefaultContextBudget = 2400;

    public static string Prompt(
        string prefixText,
        string? userName = null,
        IReadOnlyList<string>? customRules = null,
        string? languageInstruction = null,
        string? extendedContext = null,
        int contextBudget = DefaultContextBudget)
    {
        var trimmedPrefix = TrimTrailingWhitespace(prefixText);

        var sections = new List<PromptSection>();
        if (PersonaLine(userName) is { } persona)
        {
            sections.Add(ContextSection("persona", persona, priority: 60, maxChars: 200));
        }
        if (StyleLine(customRules) is { } style)
        {
            sections.Add(ContextSection("style", style, priority: 55, maxChars: 300));
        }
        if (NonEmpty(languageInstruction) is { } language)
        {
            sections.Add(ContextSection("language", language, priority: 50, maxChars: 300));
        }
        if (NonEmpty(extendedContext) is { } notes)
        {
            sections.Add(ContextSection(
                "notes", $"Notes the writer keeps in mind: {notes}", priority: 40, maxChars: 600));
        }
        // The caret prefix: top priority so it is never starved, kept by its
        // END (the text nearest the caret), and rendered last with no label so
        // the model continues from where the user stopped.
        sections.Add(new PromptSection(
            "prefix",
            trimmedPrefix,
            Priority: 100,
            MinChars: 1,
            MaxChars: Math.Max(1, trimmedPrefix.Length),
            SectionTruncation.PreserveEnd));

        var kept = PromptSectionBudget.Allocate(sections, contextBudget);
        var prefix = kept.FirstOrDefault(s => s.Name == "prefix")?.Content ?? trimmedPrefix;
        var preface = kept.Where(s => s.Name != "prefix").Select(s => s.Content).ToList();

        if (preface.Count == 0)
        {
            // No context to condition on: hand the model the bare text.
            return prefix;
        }
        // A blank line separates the conditioning preface from the live text
        // without a label the model could copy. The prefix remains the final
        // bytes of the prompt.
        return string.Join("\n", preface) + "\n\n" + prefix;
    }

    private static PromptSection ContextSection(string name, string content, int priority, int maxChars) =>
        new(name, content, priority, MinChars: 0, maxChars, SectionTruncation.PreserveStart);

    /// <summary>"Written by &lt;name&gt;." or null. Conditions the voice via authorship framing.</summary>
    private static string? PersonaLine(string? userName) =>
        NonEmpty(userName) is { } name ? $"Written by {name}." : null;

    /// <summary>"Writing style: &lt;rules&gt;." or null.</summary>
    private static string? StyleLine(IReadOnlyList<string>? customRules)
    {
        if (customRules is null) return null;
        var rules = customRules
            .Select(r => r.Trim())
            .Where(r => r.Length > 0)
            .ToList();
        return rules.Count == 0 ? null : $"Writing style: {string.Join(", ", rules)}.";
    }

    /// <summary>Description of the writer's languages, phrased as conditioning
    /// context (not an instruction — base models condition, they don't obey).</summary>
    public static string? LanguageInstruction(IReadOnlyList<string>? languages)
    {
        if (languages is null) return null;
        var langs = languages.Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        if (langs.Count == 0) return null;
        return langs.Count == 1
            ? $"The text is written in {langs[0]}."
            : $"The text is written in {string.Join(", ", langs[..^1])} or {langs[^1]}.";
    }

    private static string? NonEmpty(string? text)
    {
        var trimmed = (text ?? string.Empty).Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    /// <summary>Drops trailing spaces, tabs, and newlines so the base-model
    /// prompt ends at a word boundary.</summary>
    public static string TrimTrailingWhitespace(string text)
    {
        int end = text.Length;
        while (end > 0 && char.IsWhiteSpace(text[end - 1])) end--;
        return text[..end];
    }

    /// <summary>
    /// Windows the raw caret prefix the way the macOS factory does for the
    /// llama engine: take the last <paramref name="maxChars"/> characters,
    /// split on whitespace, and keep the last <paramref name="maxWords"/>
    /// words joined by single spaces. Collapsing whitespace keeps the prompt
    /// token count predictable.
    /// </summary>
    public static string WindowPrefix(string precedingText, int maxWords = 50, int maxChars = 1000)
    {
        string window = precedingText.Length <= maxChars
            ? precedingText
            : precedingText[^maxChars..];
        var words = window.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return window;
        var kept = words.Length <= maxWords ? words : words[^maxWords..];
        return string.Join(' ', kept);
    }
}
