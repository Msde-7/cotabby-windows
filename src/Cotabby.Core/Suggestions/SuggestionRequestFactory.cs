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
    // Ultra-short on CPU. The user pressed Enter ~880ms after a request
    // fired; the 1.5B Q4 model on CPU needs ~500ms for prompt eval alone
    // before yielding the first token, so a 12-token cap still pushed
    // total latency past 1.2s — long enough for the user to send their
    // message and cancel everything. Capping at 4-6 tokens means the
    // first chunk lands by ~600ms and the full suggestion by ~800ms.
    private const int DefaultMaxTokensSingleLine = 4;
    private const int DefaultMaxTokensMultiLine = 6;

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
