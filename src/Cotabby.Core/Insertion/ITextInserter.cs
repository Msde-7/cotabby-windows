using Cotabby.Core.Focus;

namespace Cotabby.Core.Insertion;

/// <summary>
/// Inserts text into the focused field.
/// </summary>
public interface ITextInserter
{
    /// <summary>
    /// Insert <paramref name="text"/> at the caret of <paramref name="target"/>.
    /// Returns false if the insertion was rejected. Implementations must tolerate a second
    /// call while the first is still in flight, because accepts can arrive in quick succession.
    /// </summary>
    Task<bool> InsertAsync(FocusedField target, string text, CancellationToken ct);

    /// <summary>
    /// Delete <paramref name="deleteCount"/> UTF-16 units before the caret and insert
    /// <paramref name="text"/> in their place.
    /// </summary>
    Task<bool> ReplaceAsync(FocusedField target, int deleteCount, string text, CancellationToken ct);
}
