using Cotabby.Core.Focus;

namespace Cotabby.Core.Suggestions;

/// <summary>
/// One request's lookahead while the user keeps typing. Keys arrive before the host shows them, so
/// characters that match the prediction are only recorded here, and the answer is trimmed by them
/// once the live field holds exactly that text. Times are seconds on a monotonic clock.
/// </summary>
/// <remarks>
/// The model gets a short grace period to catch up with typing, which starts at the first key it
/// does not cover, so steady typing cannot postpone a fresh request forever. Thread safe: keys
/// arrive on the UI context and partial results on the decode thread.
/// </remarks>
public sealed class TypingPrediction(FocusedField field, long sequence, CancellationToken token)
{
    public const double CatchUpWindow = 0.15;
    public const double PublicationWindow = 0.4;
    public const int MaximumTypedCharacters = 64;

    private readonly object _gate = new();
    private string _typed = "";
    private string? _prediction;
    private bool _final;
    private double? _behindSince;
    private double? _lastInputAt;
    private long _sequence = sequence;
    private bool _closed;

    /// <summary>The field the request was built from.</summary>
    public FocusedField Field { get; } = field;

    /// <summary>Cancelled when the request this prediction belongs to is.</summary>
    public CancellationToken Token { get; } = token;

    public string Typed
    {
        get { lock (_gate) return _typed; }
    }

    /// <summary>What was typed so far, with the coordinator's key count after the last key kept.</summary>
    public (string Typed, long Sequence) Progress
    {
        get { lock (_gate) return (_typed, _sequence); }
    }

    /// <summary>Keeps a typed key when the prediction, or its still-running decode, agrees with it.</summary>
    public bool Append(string characters, double time, long sequence)
    {
        if (characters.Length == 0 || characters.Any(char.IsControl) || !double.IsFinite(time)) return false;
        lock (_gate)
        {
            if (_closed || Token.IsCancellationRequested
                || _typed.Length + characters.Length > MaximumTypedCharacters
                || _behindSince is { } since && time >= since + CatchUpWindow)
            {
                return false;
            }
            string next = _typed + characters;
            string prediction = _prediction ?? "";
            bool covered = prediction.StartsWith(next, StringComparison.Ordinal);
            if (!covered && (_final || !next.StartsWith(prediction, StringComparison.Ordinal))) return false;
            _typed = next;
            _lastInputAt = time;
            _sequence = sequence;
            _behindSince = covered ? null : _behindSince ?? time;
            return true;
        }
    }

    /// <summary>
    /// Records a cumulative result. False means it contradicts what was typed and the request should
    /// start over. A partial older than one already seen is ignored, and the final one decides.
    /// </summary>
    public bool Receive(string text, bool final)
    {
        lock (_gate)
        {
            if (_final) return false;
            if (!final && _prediction is not null && !text.StartsWith(_prediction, StringComparison.Ordinal)) return true;
            bool covers = text.StartsWith(_typed, StringComparison.Ordinal);
            if (!covers && (final || !_typed.StartsWith(text, StringComparison.Ordinal))) return false;
            _prediction = text;
            _final = final;
            if (covers) _behindSince = null;
            return true;
        }
    }

    /// <summary>Stops keeping keys once its request is over, and returns what was kept.</summary>
    public string Close()
    {
        lock (_gate)
        {
            _closed = true;
            return _typed;
        }
    }

    /// <summary>True when the live field is the request's field on the way to holding what was typed.</summary>
    public bool Accepts(FocusedField live)
    {
        if (!IsSameCaretPlace(Field, live)) return false;
        string start = Normalized(Field.PrecedingText);
        string preceding = Normalized(live.PrecedingText);
        return preceding.StartsWith(start, StringComparison.Ordinal)
            && (start + Normalized(Typed)).StartsWith(preceding, StringComparison.Ordinal);
    }

    public bool IsPublished(FocusedField live) =>
        Accepts(live) && Normalized(live.PrecedingText) == Normalized(Field.PrecedingText + Typed);

    /// <summary>When the model or the host has run out of time, or null while nothing is waited on.</summary>
    public double? Expiration(FocusedField live)
    {
        bool published = IsPublished(live);
        lock (_gate)
        {
            double? model = _behindSince + CatchUpWindow;
            double? host = published ? null : _lastInputAt + PublicationWindow;
            return model is null ? host : host is null ? model : Math.Min(model.Value, host.Value);
        }
    }

    /// <summary>The answer with the typed characters taken off, once the live field shows them.</summary>
    public string? Rebased(string text, FocusedField live)
    {
        string typed = Typed;
        return IsPublished(live) && text.StartsWith(typed, StringComparison.Ordinal) ? text[typed.Length..] : null;
    }

    /// <summary>
    /// True when the live field is still the one an answer was made for: the same field, not a
    /// password field, with nothing selected and the same text after the caret. Only the text
    /// before the caret may differ, by what the user typed since.
    /// </summary>
    public static bool IsSameCaretPlace(FocusedField field, FocusedField live) =>
        !live.IsSecure && !live.HasSelection && live.IsSameField(field)
        && Normalized(live.TrailingText) == Normalized(field.TrailingText);

    private static string Normalized(string text) => SuggestionSessionReconciler.ComparisonNormalized(text);
}
