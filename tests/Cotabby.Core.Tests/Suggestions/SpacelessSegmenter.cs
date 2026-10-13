using Cotabby.Core.Suggestions;

namespace Cotabby.Core.Tests.Suggestions;

/// <summary>
/// Stands in for ICU dictionary segmentation until disposed. A run that starts with
/// one of the given words breaks after it. Any other run has no word.
/// </summary>
internal sealed class SpacelessSegmenter : IDisposable
{
    /// <summary>Tests that chunk space-less text share this collection because the segmenter is static.</summary>
    public const string Collection = "AcceptanceChunker.SpacelessWordEnd";

    private readonly Func<string, int, int, int?> _previous = AcceptanceChunker.SpacelessWordEnd;

    public SpacelessSegmenter(params string[] firstWords) =>
        AcceptanceChunker.SpacelessWordEnd = (text, start, _) =>
            firstWords.FirstOrDefault(word => string.CompareOrdinal(text, start, word, 0, word.Length) == 0) is { } word
                ? start + word.Length
                : null;

    public void Dispose() => AcceptanceChunker.SpacelessWordEnd = _previous;
}

[CollectionDefinition(SpacelessSegmenter.Collection)]
public sealed class SpacelessSegmenterCollection;
