namespace Cotabby.Win32.Focus;

/// <summary>
/// Decides whether the text before the caret of a long document is read through a range that
/// starts a bounded distance before the caret, instead of from the document's start.
/// </summary>
/// <remarks>
/// A read from the start copies the whole document up to the caret across processes on every
/// keystroke, only for all but the last <see cref="Kept"/> characters to be thrown away. A field
/// whose last full read was long gets the bounded read instead. That read moves a range endpoint
/// by paragraph, which no provider has been measured on, so a field where it is slower than the
/// full read was, or where it did not reach far enough, goes back to full reads for as long as it
/// keeps focus. Short documents are always read in full, exactly as before.
/// </remarks>
public sealed class PrecedingTextReads
{
    /// <summary>Text kept before the caret. The request factory sends at most this much prefix.</summary>
    public const int Kept = 14000;

    /// <summary>A full read longer than this makes the field's next reads bounded.</summary>
    public const int LongDocumentChars = 64 * 1024;

    private readonly object _gate = new();
    private string? _fieldId;
    private bool _long;
    private bool _boundedFailed;
    private TimeSpan _fullReadTime;

    /// <summary>True when the next read of <paramref name="fieldId"/> should be bounded.</summary>
    public bool UseBounded(string fieldId)
    {
        lock (_gate) return fieldId == _fieldId && _long && !_boundedFailed;
    }

    /// <param name="length">The length of everything before the caret.</param>
    public void FullRead(string fieldId, int length, TimeSpan elapsed)
    {
        lock (_gate)
        {
            if (fieldId != _fieldId)
            {
                _fieldId = fieldId;
                _boundedFailed = false;
            }
            _long = length > LongDocumentChars;
            _fullReadTime = elapsed;
        }
    }

    /// <param name="sufficed">False when the bounded range was too short and a full read was needed after all.</param>
    public void BoundedRead(string fieldId, bool sufficed, TimeSpan elapsed)
    {
        lock (_gate)
        {
            if (fieldId != _fieldId) return;
            if (!sufficed || elapsed > _fullReadTime) _boundedFailed = true;
        }
    }

    /// <summary>The last <see cref="Kept"/> characters of the text before the caret, never starting inside a surrogate pair.</summary>
    public static string KeepEnd(string before)
    {
        if (before.Length <= Kept) return before;
        before = before[^Kept..];
        return char.IsLowSurrogate(before[0]) ? before[1..] : before;
    }

    /// <summary>
    /// The text a bounded read gives, or null when it must be read in full: a range that is
    /// shorter than <see cref="Kept"/> holds everything before the caret only when it starts at
    /// the start of the document.
    /// </summary>
    public static string? FromBounded(string bounded, bool startsAtDocumentStart) =>
        bounded.Length > Kept || startsAtDocumentStart ? KeepEnd(bounded) : null;
}
