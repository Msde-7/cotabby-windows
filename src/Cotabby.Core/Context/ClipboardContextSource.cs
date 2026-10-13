using Cotabby.Core.Focus;
using Cotabby.Core.Suggestions;
using Cotabby.Core.Support;

namespace Cotabby.Core.Context;

/// <summary>
/// Decides which copied text, if any, may go into a prompt. An accepted verdict is reused while
/// the host and the clipboard stay the same, which keeps the head of the prompt stable for the
/// engine. A rejected one is checked again, because more typing can make it relevant.
/// </summary>
public sealed class ClipboardContextSource(IClipboardReader reader, TimeProvider clock)
{
    // Predictions overlap briefly when one replaces another, so the filter and its memo share a lock.
    private readonly object _gate = new();
    private readonly ClipboardRelevanceFilter _filter = new(clock);
    private Verdict? _verdict;

    private sealed record Verdict(int ProcessId, uint SequenceNumber, string Text);

    public string? For(FocusedField field)
    {
        lock (_gate)
        {
            uint sequence = reader.SequenceNumber;
            if (_verdict is { } verdict && verdict.ProcessId == field.ProcessId && verdict.SequenceNumber == sequence)
            {
                return verdict.Text;
            }

            string copied = ScalarSets.TrimWhitespaceAndNewlines(reader.ReadText() ?? "");
            string? relevant = _filter.Filter(
                copied.Length == 0 ? null : copied, sequence, SuggestionRequestFactory.Prefix(field));
            _verdict = relevant is null ? null : new Verdict(field.ProcessId, sequence, relevant);
            return relevant;
        }
    }
}
