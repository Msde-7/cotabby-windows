using Cotabby.Core.Focus;

namespace Cotabby.Core.Suggestions;

/// <summary>
/// Pure builder converting a focus snapshot into a <see cref="SuggestionRequest"/>.
/// Mirrors the macOS port's <c>SuggestionRequestFactory</c>. No I/O, no DI —
/// trivially testable.
/// </summary>
public static class SuggestionRequestFactory
{
    // Smaller prefix window than the macOS port (which can lean on KV cache reuse
    // across keystrokes). LLamaSharp's StatelessExecutor re-evaluates the entire
    // prompt every request, so on CPU a 1024-char prompt forces ~600ms of prompt
    // eval before the first token is sampled — long enough that the next
    // keystroke routinely cancels the request before any chunk lands. 512 chars
    // (~150 tokens) keeps prompt eval under ~200ms and still preserves enough
    // local context for Qwen-Coder to produce a coherent continuation.
    private const int MaxPrefixChars = 512;
    private const int MaxSuffixChars = 192;
    // Token budgets sized to the macOS "7-12 words" preset (one chunk past the
    // shortest preset's 5-token cap). Higher than the previous 4/6 — those were
    // so small that completions were barely a word, which made suggestions feel
    // useless when one DID land. Code completion benefits from a larger budget
    // because BPE packs prose tokens looser than code tokens.
    private const int DefaultMaxTokensSingleLine = 16;
    private const int DefaultMaxTokensMultiLine = 24;

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
