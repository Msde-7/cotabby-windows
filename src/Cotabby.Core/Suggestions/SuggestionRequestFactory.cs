using Cotabby.Core.Focus;

namespace Cotabby.Core.Suggestions;

/// <summary>
/// Pure builder converting a focus snapshot into a <see cref="SuggestionRequest"/>.
/// Mirrors the macOS port's <c>SuggestionRequestFactory</c>. No I/O, no DI —
/// trivially testable.
/// </summary>
public static class SuggestionRequestFactory
{
    private const int MaxPrefixChars = 1024;
    private const int MaxSuffixChars = 512;
    // Conservative caps — long suggestions on small models are usually
    // degenerate. Users can always re-trigger by typing the first char.
    private const int DefaultMaxTokensSingleLine = 24;
    private const int DefaultMaxTokensMultiLine = 48;

    public static SuggestionRequest Build(FocusedField field, string requestId)
    {
        var caret = Math.Clamp(field.CaretOffset, 0, field.Text.Length);
        var prefixStart = Math.Max(0, caret - MaxPrefixChars);
        var prefix = field.Text.Substring(prefixStart, caret - prefixStart);

        var suffixEnd = Math.Min(field.Text.Length, caret + MaxSuffixChars);
        var suffix = field.Text.Substring(caret, suffixEnd - caret);

        return new SuggestionRequest
        {
            RequestId = requestId,
            Prefix = prefix,
            Suffix = suffix,
            HostApp = field.ProcessName,
            SingleLine = field.IsSingleLine,
            MaxTokens = field.IsSingleLine ? DefaultMaxTokensSingleLine : DefaultMaxTokensMultiLine,
        };
    }
}
