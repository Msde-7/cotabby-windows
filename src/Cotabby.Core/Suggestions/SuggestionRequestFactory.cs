using Cotabby.Core.Focus;
using Cotabby.Core.Support;

namespace Cotabby.Core.Suggestions;

/// <summary>Pure builder converting a focus snapshot into a <see cref="SuggestionRequest"/>.</summary>
public static class SuggestionRequestFactory
{
    private const int MaxPrefixChars = 14000;
    private const int MaxSuffixChars = 192;

    public static SuggestionRequest Build(FocusedField field, string requestId, SuggestionSettings settings)
    {
        var caret = Math.Clamp(field.CaretOffset, 0, field.Text.Length);
        var prefixStart = Math.Max(0, caret - MaxPrefixChars);
        var suffixEnd = Math.Min(field.Text.Length, caret + MaxSuffixChars);
        bool singleLine = field.IsSingleLine || !settings.AllowMultiLine;

        return new SuggestionRequest
        {
            RequestId = requestId,
            Prefix = field.Text[prefixStart..caret],
            Suffix = field.Text[caret..suffixEnd],
            HostApp = field.ProcessName,
            SingleLine = singleLine,
            MaxTokens = settings.MaxTokensFor(singleLine),
            UserName = settings.UserName,
            LanguageInstruction = BaseCompletionPromptRenderer.LanguageInstruction(settings.Languages) ?? "",
        };
    }
}
