namespace Cotabby.Core.Support;

/// <summary>
/// Renders the prompt for the base-model completion pipeline.
/// <para>
/// A base model has no instruction-following channel and will continue a bare "Task:" line as if
/// it were the document. This renderer treats the model as a pure text continuer. Persona, style,
/// language and supporting context are folded into a short conditioning preface, and the exact
/// caret prefix is the last thing in the prompt. Spaces, line breaks and indentation remain part of
/// the text the model continues.
/// </para>
/// Sections are budgeted through <see cref="PromptSectionBudget"/> so a large glossary, clipboard
/// or screen capture can never crowd out the caret text.
/// </summary>
public static class BaseCompletionPromptRenderer
{
    /// <summary>Total character budget for the preface plus caret prefix.</summary>
    public const int DefaultContextBudget = 6400;

    private const int HistoryMaxCharacters = 760;

    /// <param name="prefixText">The windowed text before the caret, rendered last and verbatim.</param>
    /// <param name="userName">The writer's name, stated only at a sign-off.</param>
    /// <param name="trailingText">Text after the caret, quoted as reference material.</param>
    /// <param name="maxSuffixCharacters">Bound on the quoted trailing text.</param>
    /// <param name="customRules">User style rules.</param>
    /// <param name="extendedContext">Free-form notes from the user.</param>
    /// <param name="languageInstruction">Language conditioning sentence.</param>
    /// <param name="clipboardContext">Distilled clipboard text.</param>
    /// <param name="visualContextSummary">Text captured near the field.</param>
    /// <param name="surfaceLines">Composed description of the writing surface, one fact group per line.</param>
    /// <param name="historyExamples">Earlier sentences by the same writer.</param>
    /// <param name="contextBudget">Character budget, used when no token budget is given.</param>
    /// <param name="maxScreenCharacters">Cap on the labelled screen section.</param>
    /// <param name="screenPriority">Fill priority of the screen section.</param>
    /// <param name="tokenBudget">When given, sections fill against estimated tokens instead of characters.</param>
    public static string Prompt(
        string prefixText,
        string? userName = null,
        string trailingText = "",
        int maxSuffixCharacters = 192,
        IReadOnlyList<string>? customRules = null,
        string? extendedContext = null,
        string? languageInstruction = null,
        string? clipboardContext = null,
        string? visualContextSummary = null,
        IReadOnlyList<string>? surfaceLines = null,
        IReadOnlyList<string>? historyExamples = null,
        int contextBudget = DefaultContextBudget,
        int maxScreenCharacters = 4000,
        int screenPriority = 45,
        int? tokenBudget = null)
    {
        var sections = new List<PromptSection>();
        // The surface description leads the preface. Knowing the writing surface is the strongest
        // situational cue a base model gets.
        if (surfaceLines is { Count: > 0 })
        {
            sections.Add(ContextSection("surface", string.Join(' ', surfaceLines), priority: 70, maxChars: 240));
        }
        if (PersonaLine(userName, prefixText) is { } persona)
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
            // The cap must stay at or above the settings limit for extended context plus this
            // label, so the full user-entered text survives.
            sections.Add(ContextSection(
                "notes", $"Notes the writer keeps in mind: {notes}", priority: 40, maxChars: 1300));
        }
        if (HistorySection(historyExamples) is { } history)
        {
            sections.Add(history);
        }
        if (NonEmpty(clipboardContext) is { } clip)
        {
            sections.Add(ContextSection("clipboard", $"On the clipboard: {clip}", priority: 35, maxChars: 400));
        }
        if (NonEmpty(visualContextSummary) is { } screen)
        {
            sections.Add(ContextSection(
                "screen", $"Nearby on screen: {screen}", priority: screenPriority, maxChars: maxScreenCharacters));
        }
        string followingText = new GraphemeString(trailingText).Prefix(Math.Max(0, maxSuffixCharacters));
        if (!ScalarSets.IsBlank(followingText))
        {
            // Base models share no universal fill-in-the-middle format. The existing ending is
            // described as quoted reference material and the caret prefix stays last. The section
            // is all or nothing, so a tight budget cannot leave an unfinished label or quote.
            sections.Add(WholeSection("following", $"Later in the same passage:\n“{followingText}”", priority: 80)
                with { PreservesWhitespace = true });
        }
        // The caret prefix has top priority so it is never starved, is kept by its end (the text
        // nearest the caret), and is rendered last with no label.
        sections.Add(new PromptSection(
            "prefix",
            prefixText,
            Priority: 100,
            MinChars: 1,
            MaxChars: Math.Max(1, GraphemeString.CountOf(prefixText)),
            SectionTruncation.PreserveEnd,
            PreservesWhitespace: true));

        var kept = tokenBudget is { } tokens
            ? PromptSectionBudget.Allocate(sections, tokens, TokenCountEstimator.Estimate)
            : PromptSectionBudget.Allocate(sections, contextBudget);
        string prefix = kept.FirstOrDefault(s => s.Name == "prefix")?.Content ?? string.Empty;
        var preface = kept.Where(s => s.Name != "prefix").Select(s => s.Content).ToList();

        // With no context to condition on the model gets the bare text. Otherwise a blank line
        // separates the preface from the live text without a label the model could copy.
        return preface.Count == 0 ? prefix : string.Join('\n', preface) + "\n\n" + prefix;
    }

    /// <summary>
    /// The writer's own earlier sentences, quoted. Examples are dropped whole until the section fits
    /// its cap, because a trimmed section could end on an unclosed quote and the model would read
    /// the live caret text as part of it.
    /// </summary>
    private static PromptSection? HistorySection(IReadOnlyList<string>? examples)
    {
        const string heading = "Earlier writing by the same author:";
        string content = heading;
        int count = heading.Length;
        foreach (string example in examples ?? [])
        {
            string trimmed = ScalarSets.TrimWhitespaceAndNewlines(example);
            if (trimmed.Length == 0) continue;
            string line = $"\n“{trimmed}”";
            int lineCount = GraphemeString.CountOf(line);
            if (count + lineCount > HistoryMaxCharacters) continue;
            content += line;
            count += lineCount;
        }
        return count > heading.Length ? WholeSection("history", content, priority: 38) : null;
    }

    private static PromptSection ContextSection(string name, string content, int priority, int maxChars) =>
        new(name, content, priority, MinChars: 0, maxChars, SectionTruncation.PreserveStart);

    /// <summary>A section that is included in full or not at all.</summary>
    private static PromptSection WholeSection(string name, string content, int priority)
    {
        int count = GraphemeString.CountOf(content);
        return new(name, content, priority, MinChars: count, MaxChars: count, SectionTruncation.PreserveStart);
    }

    /// <summary>
    /// "Written by name." when the caret follows a valediction, null otherwise. At a sign-off the
    /// name is the one token wanted, and the closing line anchors the model so nothing else leaks.
    /// </summary>
    private static string? PersonaLine(string? userName, string prefix) =>
        NonEmpty(userName) is { } name && SignOffCue.PrecedesSignature(prefix) ? $"Written by {name}." : null;

    /// <summary>"Writing style: rules." or null, on its own line so sentence-shaped rules read correctly.</summary>
    private static string? StyleLine(IReadOnlyList<string>? customRules)
    {
        var rules = (customRules ?? [])
            .Select(ScalarSets.TrimWhitespaceAndNewlines)
            .Where(rule => rule.Length > 0)
            .ToList();
        return rules.Count == 0 ? null : $"Writing style: {string.Join(", ", rules)}.";
    }

    private static string? NonEmpty(string? text)
    {
        string trimmed = ScalarSets.TrimWhitespaceAndNewlines(text ?? string.Empty);
        return trimmed.Length == 0 ? null : trimmed;
    }
}
