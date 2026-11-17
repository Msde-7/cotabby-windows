using Cotabby.Core.Support;
using Cotabby.Core.Context;
using Cotabby.Core.Focus;
using Cotabby.Core.Text;

namespace Cotabby.Core.Suggestions;

/// <summary>Pure builder converting a focus snapshot into a <see cref="SuggestionRequest"/>.</summary>
public static class SuggestionRequestFactory
{
    private const int MaxPrefixChars = 14000;
    private const int MaxPrefixWords = 2400;
    private const int MaxSuffixChars = 192;
    private const int MaxClipboardContextCharacters = 1200;
    private const string ClipMarker = "...";

    /// <summary>
    /// True when the text before the caret is worth continuing: some non-whitespace, and when
    /// suggestions wait for a finished word, no word still being typed. Scripts without spaces
    /// and code-like tokens never count as unfinished, so they are not starved.
    /// </summary>
    public static bool ShouldGenerate(string preceding, bool suggestWithinWords = true) =>
        !string.IsNullOrWhiteSpace(preceding)
        && (suggestWithinWords || Support.CaretWordContext.UnfinishedWord(preceding) is null);

    /// <summary>The full gate before generating: <see cref="ShouldGenerate(string, bool)"/> and a caret outside a token.</summary>
    public static bool ShouldGenerate(string preceding, string trailing, bool suggestWithinWords = true) =>
        ShouldGenerate(preceding, suggestWithinWords) && !Support.CaretTokenPosition.IsInsideToken(preceding, trailing);

    /// <summary>The windowed text before the caret, as a request carries it.</summary>
    public static string Prefix(FocusedField field) => Prefix(field.PrecedingText);

    /// <summary>
    /// The last <paramref name="maxCharacters"/> characters, cut further to the last
    /// <paramref name="maxWords"/> words. The cut keeps the original whitespace, since line breaks
    /// and the space before the caret matter to the continuation.
    /// </summary>
    public static string Prefix(string preceding, int maxCharacters = MaxPrefixChars, int maxWords = MaxPrefixWords)
    {
        if (maxCharacters <= 0 || maxWords <= 0) return "";
        string window = preceding.Length <= maxCharacters
            ? preceding
            : new GraphemeString(preceding).Suffix(maxCharacters);

        // Walk back from the end and stop at the start of the word one past the budget.
        int words = 0;
        int i = window.Length;
        while (i > 0)
        {
            while (i > 0 && char.IsWhiteSpace(window[i - 1])) i--;
            if (i == 0) break;
            if (words == maxWords)
            {
                int end = i;
                while (end < window.Length && char.IsWhiteSpace(window[end])) end++;
                return window[end..];
            }
            while (i > 0 && !char.IsWhiteSpace(window[i - 1])) i--;
            words++;
        }
        return window;
    }

    /// <param name="clipboardContext">Clipboard text that already passed the relevance filter, or null.</param>
    /// <param name="historyExamples">Passages of the user's past writing, dropped for the endpoint.</param>
    /// <param name="screenContext">Recognized screen text for this field, or null.</param>
    public static SuggestionRequest Build(
        FocusedField field, string requestId, SuggestionSettings settings, string? clipboardContext = null,
        IReadOnlyList<string>? historyExamples = null, string? screenContext = null)
    {
        string trailing = field.TrailingText;
        bool singleLine = field.IsSingleLine || !settings.AllowMultiLine;
        string prefix = Prefix(field);

        return new SuggestionRequest
        {
            RequestId = requestId,
            Prefix = prefix,
            Suffix = trailing[..Math.Min(trailing.Length, MaxSuffixChars)],
            SingleLine = singleLine,
            MaxTokens = settings.MaxTokensFor(singleLine),
            UserName = settings.UserName,
            LanguageInstruction = LanguageCatalog.PromptInstruction(settings.Languages) ?? "",
            ExtendedContext = settings.ExtendedContext.Trim(),
            ClipboardContext = ActiveClipboardContext(clipboardContext, settings, prefix),
            ScreenContext = ActiveScreenContext(screenContext, settings),
            SurfaceLines = settings.SurfaceContext ? SurfaceLines(field) : [],
            WordRange = settings.WordRange,
            // Typing history stays on this PC. The provider already returns nothing for the
            // endpoint, and dropping it here keeps that guarantee in the one pure place every
            // request passes through.
            HistoryExamples = settings.UsesEndpoint ? [] : historyExamples ?? [],
        };
    }

    private static string ActiveClipboardContext(string? rawContext, SuggestionSettings settings, string prefix)
    {
        if (!settings.ClipboardContext || rawContext is null) return "";

        string sanitized = PromptContextSanitizer.Sanitize(rawContext);
        if (!PromptContextSanitizer.ContainsAlphanumericSignal(sanitized)) return "";

        return ClippedText(ClipboardContentDistiller.Distill(sanitized, prefix), MaxClipboardContextCharacters);
    }

    /// <summary>
    /// The screen excerpt bounded for the engine. CJK and code cost far more tokens per character
    /// than English, so the local excerpt is also held to an estimated token allowance that leaves
    /// room for the caret text and the other context.
    /// </summary>
    internal static string ActiveScreenContext(string? rawContext, SuggestionSettings settings)
    {
        if (!settings.ScreenContext || rawContext is null) return "";
        int limit = VisualContextConfiguration.For(settings.UsesEndpoint).MaxSummaryCharacters;
        string sanitized = PromptContextSanitizer.Sanitize(rawContext, limit);
        if (!settings.UsesEndpoint)
        {
            while (sanitized.Length > 0 && ScreenTokenCost(sanitized) > MaxLocalScreenTokens)
            {
                var graphemes = new GraphemeString(sanitized);
                sanitized = ScalarSets.TrimWhitespaceAndNewlines(graphemes.Prefix(graphemes.Count * 9 / 10));
            }
        }
        return PromptContextSanitizer.ContainsAlphanumericSignal(sanitized) ? sanitized : "";
    }

    private const int MaxLocalScreenTokens = 1200;

    private static int ScreenTokenCost(string text) =>
        TokenCountEstimator.Estimate(text) + text.EnumerateRunes().Count(r => !r.IsAscii) * 2;

    private static string ClippedText(string text, int maxCharacters)
    {
        var graphemes = new GraphemeString(text);
        if (graphemes.Count <= maxCharacters) return text;
        string kept = graphemes.Prefix(Math.Max(maxCharacters - ClipMarker.Length, 0));
        return ScalarSets.TrimWhitespaceAndNewlines(kept) + ClipMarker;
    }

    private static IReadOnlyList<string> SurfaceLines(FocusedField field)
    {
        var surface = SurfaceContextComposer.Compose(
            AppSurfaceClassifier.Classify(
                field.ProcessName, TerminalAppDetector.IsIntegratedTerminal(field.ClassName)),
            applicationName: field.AppDisplayName.Length > 0 ? field.AppDisplayName : field.ProcessName,
            windowTitle: field.WindowTitle,
            fieldPlaceholder: field.HelpText);
        return surface is null ? [] : SurfaceContextComposer.PrefaceLines(surface);
    }
}
