using System.Text.RegularExpressions;

namespace Cotabby.Core.Support;

/// <summary>
/// The chat and control-token scaffolding that base models occasionally leak into their output, and
/// the rule for removing it from a raw completion. Base model vocabularies still contain the special
/// tokens of the chat templates they were trained alongside, and a drifting model can emit them as
/// literal text.
/// <list type="bullet">
/// <item>Role-header blocks wrap a role name between two markers, so the whole block is stripped.</item>
/// <item>Opening and role markers wrap content. Only the marker is removed and adjacent text stays.</item>
/// <item>Stop markers mean the model should have stopped. The completion is truncated at the first one.</item>
/// <item>An opening marker at the start of a line, after text, is a new turn. The continuation ended
/// with the line before it.</item>
/// </list>
/// Only unambiguous control tokens are listed, so removing them cannot eat legitimate prose or code.
/// Reasoning blocks are handled by the normalizer so the text around them survives.
/// </summary>
public static partial class ControlTokenMarkers
{
    /// <summary>A Llama-3 role-header block, with the role name between its two markers.</summary>
    [GeneratedRegex(@"<\|start_header_id\|>.*?<\|end_header_id\|>")]
    private static partial Regex RoleHeaderBlock();

    /// <summary>
    /// Opening and role markers, removed in place. The Llama-3 header markers stay here as a fallback
    /// for an unpaired marker the block pattern cannot match.
    /// </summary>
    public static readonly IReadOnlyList<string> OpeningMarkers =
    [
        "<|im_start|>",
        "<start_of_turn>",
        "<|user|>",
        "<|assistant|>",
        "<|system|>",
        "<|start_header_id|>",
        "<|end_header_id|>",
        "[INST]",
        "[/INST]",
    ];

    /// <summary>
    /// Stop and end-of-turn markers. The Llama and Mistral end-of-sequence tag is excluded because it
    /// is also the closing tag of the HTML strikethrough element.
    /// </summary>
    public static readonly IReadOnlyList<string> StopMarkers =
    [
        "<|im_end|>",
        "<|endoftext|>",
        "<|end|>",
        "<end_of_turn>",
        "<|eot_id|>",
    ];

    /// <summary>
    /// Removes role-header blocks and opening markers, then truncates at the first stop marker.
    /// </summary>
    public static string Sanitize(string text)
    {
        string result = RoleHeaderBlock().Replace(text, string.Empty);

        // A turn that opens on a new line after text is the model writing the next turn of a
        // transcript it saw. Its role word and question are no continuation.
        if (FirstLineStartOpeningMarker(result) is { } turnStart) result = result[..turnStart];

        foreach (string marker in OpeningMarkers)
        {
            result = result.Replace(marker, string.Empty, StringComparison.Ordinal);
        }

        if (FirstStopMarker(result) is { } stop) result = result[..stop];
        return result;
    }

    /// <summary>
    /// The earliest opening marker that starts a line after some text (only spaces or tabs between it
    /// and the line break before it), or null.
    /// </summary>
    private static int? FirstLineStartOpeningMarker(string text)
    {
        int? earliest = null;
        foreach (string marker in OpeningMarkers)
        {
            int searchStart = 0;
            int found;
            while ((found = text.IndexOf(marker, searchStart, StringComparison.Ordinal)) >= 0)
            {
                int lineEnd = found;
                while (lineEnd > 0 && text[lineEnd - 1] is ' ' or '\t') lineEnd--;
                if (lineEnd > 0 && ScalarSets.IsNewline(text[lineEnd - 1])
                    && !string.IsNullOrWhiteSpace(text[..lineEnd]))
                {
                    earliest = Math.Min(earliest ?? found, found);
                    break;
                }
                searchStart = found + marker.Length;
            }
        }
        return earliest;
    }

    private static int? FirstStopMarker(string text)
    {
        int? earliest = null;
        foreach (string marker in StopMarkers)
        {
            int found = text.IndexOf(marker, StringComparison.Ordinal);
            if (found >= 0) earliest = Math.Min(earliest ?? found, found);
        }
        return earliest;
    }
}
