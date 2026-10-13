namespace Cotabby.Core.Context;

/// <summary>
/// Decides whether the clipboard is relevant enough to go into the prompt. It must have been
/// copied recently while Cotabby was running, and it must share a word with the text being written.
/// </summary>
/// <remarks>
/// The first observation only records a baseline. The sequence number is a cumulative counter, so
/// it cannot say how old the contents are, and text copied before startup stays out until the user
/// copies again. Not thread safe.
/// </remarks>
public sealed class ClipboardRelevanceFilter
{
    public static readonly TimeSpan StaleThreshold = TimeSpan.FromSeconds(300);

    private const int MinimumTokenLength = 3;

    private readonly TimeProvider _clock;
    private uint? _lastKnownSequenceNumber;
    private DateTimeOffset? _lastChange;

    public ClipboardRelevanceFilter(TimeProvider? clock = null) => _clock = clock ?? TimeProvider.System;

    /// <summary>Returns <paramref name="clipboard"/> unchanged when it looks relevant, otherwise null.</summary>
    public string? Filter(string? clipboard, uint sequenceNumber, string precedingText)
    {
        if (clipboard is null) return null;

        if (_lastKnownSequenceNumber is not { } baseline)
        {
            _lastKnownSequenceNumber = sequenceNumber;
            return null;
        }

        if (sequenceNumber != baseline)
        {
            _lastKnownSequenceNumber = sequenceNumber;
            _lastChange = _clock.GetUtcNow();
        }

        if (_lastChange is not { } changed || _clock.GetUtcNow() - changed >= StaleThreshold) return null;

        return Tokens(clipboard).Overlaps(Tokens(precedingText)) ? clipboard : null;
    }

    private static HashSet<string> Tokens(string text) =>
        PromptContextSanitizer.SignificantTokens(text, MinimumTokenLength);
}
