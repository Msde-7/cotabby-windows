namespace Cotabby.Core.Suggestions;

/// <summary>
/// One visible suggestion. Immutable, so every transition (typing through it,
/// accepting part of it) produces a fresh instance.
/// </summary>
/// <remarks>
/// The prediction and what is offered are kept apart. An uncertain word ending can be offered
/// on its own while the words after it wait, and with following words off only one acceptance
/// chunk shows at a time. Consuming what is shown reveals more without another request.
/// </remarks>
public sealed record SuggestionSession
{
    public required string RequestId { get; init; }

    /// <summary>The predicted text not yet typed or accepted, including words not offered yet.</summary>
    public required string PredictedText { get; init; }

    /// <summary>
    /// How much of <see cref="PredictedText"/> makes up the first offer, usually a word ending.
    /// Null puts no limit on it.
    /// </summary>
    public int? FirstOfferLength { get; init; }

    /// <summary>False offers one acceptance chunk at a time from the prediction.</summary>
    public bool ShowFollowingWords { get; init; } = true;

    /// <summary>Whether a word's trailing punctuation shows with it when one chunk shows at a time.</summary>
    public bool AcceptPunctuationWithWord { get; init; } = true;

    /// <summary>Set when the session offers a spelling fix instead of a continuation.</summary>
    public TypoCorrection? Correction { get; init; }

    /// <summary>The part of the prediction offered now. Only this may be accepted.</summary>
    public string VisibleText
    {
        get
        {
            string available = FirstOfferLength is { } limit && limit < PredictedText.Length
                ? PredictedText[..limit]
                : PredictedText;
            if (ShowFollowingWords || Correction is not null) return available;
            string chunk = AcceptanceChunker.NextChunk(available, AcceptPunctuationWithWord);
            return chunk.Length > 0 ? chunk : available;
        }
    }

    /// <summary>A whitespace-only tail has nothing left worth showing.</summary>
    public bool IsExhausted => string.IsNullOrWhiteSpace(PredictedText);

    /// <summary>The session after <paramref name="count"/> characters were typed or accepted.</summary>
    public SuggestionSession Advancing(int count)
    {
        count = Math.Clamp(count, 0, PredictedText.Length);
        return this with
        {
            PredictedText = PredictedText[count..],
            FirstOfferLength = FirstOfferLength is { } limit && limit > count ? limit - count : null,
        };
    }
}

/// <summary>A misspelled word before the caret and its fix. Accepting swaps them as one unit.</summary>
public sealed record TypoCorrection(string Word, string CorrectedWord);
