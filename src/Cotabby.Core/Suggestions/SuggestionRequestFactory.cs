using Cotabby.Core.Context;
using Cotabby.Core.Focus;
using Cotabby.Core.Text;

namespace Cotabby.Core.Suggestions;

/// <summary>Pure builder converting a focus snapshot into a <see cref="SuggestionRequest"/>.</summary>
public static class SuggestionRequestFactory
{
    private const int MaxPrefixChars = 14000;
    private const int MaxSuffixChars = 192;
    private const int MaxClipboardContextCharacters = 1200;
    private const string ClipMarker = "...";

    /// <summary>The windowed text before the caret, as a request carries it.</summary>
    public static string Prefix(FocusedField field)
    {
        var preceding = field.PrecedingText;
        return preceding[Math.Max(0, preceding.Length - MaxPrefixChars)..];
    }

    /// <param name="clipboardContext">Clipboard text that already passed the relevance filter, or null.</param>
    public static SuggestionRequest Build(
        FocusedField field, string requestId, SuggestionSettings settings, string? clipboardContext = null)
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
            SurfaceLines = settings.SurfaceContext ? SurfaceLines(field) : [],
            WordRange = settings.WordRange,
        };
    }

    private static string ActiveClipboardContext(string? rawContext, SuggestionSettings settings, string prefix)
    {
        if (!settings.ClipboardContext || rawContext is null) return "";

        string sanitized = PromptContextSanitizer.Sanitize(rawContext);
        if (!PromptContextSanitizer.ContainsAlphanumericSignal(sanitized)) return "";

        return ClippedText(ClipboardContentDistiller.Distill(sanitized, prefix), MaxClipboardContextCharacters);
    }

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
            applicationName: field.ProcessName,
            windowTitle: field.WindowTitle,
            fieldPlaceholder: field.HelpText);
        return surface is null ? [] : SurfaceContextComposer.PrefaceLines(surface);
    }
}
