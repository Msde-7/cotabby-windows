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
    // Conservative caps — long suggestions on small CPU-only models take
    // too long to be useful (12 chars/sec on 1.5B Q4 = 4s for 48 tokens
    // ≈ 50 chars). Smaller caps make the suggestion arrive while the
    // context is still current; users can always re-trigger after accept.
    private const int DefaultMaxTokensSingleLine = 16;
    private const int DefaultMaxTokensMultiLine = 32;

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
