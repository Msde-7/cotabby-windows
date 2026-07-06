using Cotabby.Core.Focus;

namespace Cotabby.Core.Suggestions;

/// <summary>
/// Pure builder converting a focus snapshot into a <see cref="SuggestionRequest"/>.
/// Mirrors the macOS port's <c>SuggestionRequestFactory</c>. No I/O, no DI —
/// trivially testable.
/// </summary>
public static class SuggestionRequestFactory
{
    // Upstream llama-path windows: the engine word-windows this down to the
    // last 50 words before building the prompt, so the raw capture matches
    // the macOS maxPrefixCharacters = 1000 / maxSuffixCharacters = 192.
    private const int MaxPrefixChars = 1000;
    private const int MaxSuffixChars = 192;

    // Upstream default preset is "12-20 words" → 25 prediction tokens
    // single-line; multi-line doubles the budget capped at 60.
    public const int DefaultMaxTokensSingleLine = 25;
    public const int DefaultMaxTokensMultiLine = 50;

    /// <summary>
    /// Build a request, optionally overriding the token budget from settings
    /// (the completion length preset). When <paramref name="maxTokensSingle"/>
    /// / <paramref name="maxTokensMulti"/> are null, the defaults above apply.
    /// </summary>
    public static SuggestionRequest Build(
        FocusedField field,
        string requestId,
        int? maxTokensSingle = null,
        int? maxTokensMulti = null,
        string userName = "",
        string languageInstruction = "")
    {
        var caret = Math.Clamp(field.CaretOffset, 0, field.Text.Length);
        var prefixStart = Math.Max(0, caret - MaxPrefixChars);
        var prefix = field.Text.Substring(prefixStart, caret - prefixStart);

        var suffixEnd = Math.Min(field.Text.Length, caret + MaxSuffixChars);
        var suffix = field.Text.Substring(caret, suffixEnd - caret);

        var single = Math.Clamp(maxTokensSingle ?? DefaultMaxTokensSingleLine, 4, 96);
        var multi = Math.Clamp(maxTokensMulti ?? DefaultMaxTokensMultiLine, 4, 128);

        return new SuggestionRequest
        {
            RequestId = requestId,
            Prefix = prefix,
            Suffix = suffix,
            HostApp = field.ProcessName,
            SingleLine = field.IsSingleLine,
            MaxTokens = field.IsSingleLine ? single : multi,
            UserName = userName,
            LanguageInstruction = languageInstruction,
        };
    }
}
