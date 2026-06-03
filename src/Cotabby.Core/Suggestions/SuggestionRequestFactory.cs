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
    // Aggressively short on CPU. Throughput is ~30 tokens/sec for 1.5B Q4
    // CPU; capping at 12 multi-line / 8 single-line keeps end-to-end
    // latency under 500ms, which is the only way the cancel-prior debounce
    // pattern can ever surface a suggestion — at 32-token caps the engine
    // call took 5 seconds and every keystroke in the user's natural typing
    // cadence cancelled it before it could produce a chunk.
    private const int DefaultMaxTokensSingleLine = 8;
    private const int DefaultMaxTokensMultiLine = 12;

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
