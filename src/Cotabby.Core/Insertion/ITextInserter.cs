using Cotabby.Core.Focus;

namespace Cotabby.Core.Insertion;

/// <summary>
/// Inserts a string of text into the currently focused field. Implementations
/// prefer UIA <c>TextPattern.InsertText</c> when supported, falling back to
/// synthesized keystrokes (<c>SendInput</c>) for fields that don't expose the
/// pattern (Chrome, Electron apps, raw Win32 edits, …).
/// </summary>
public interface ITextInserter
{
    /// <summary>
    /// Insert <paramref name="text"/> at the caret of <paramref name="target"/>.
    /// Returns false if the field is no longer focused or the insertion was rejected.
    /// </summary>
    /// <remarks>
    /// Implementations must be reentrancy-safe: a Tab-driven acceptance may fire
    /// while a previous insertion is still in-flight if the user is rapid-fire.
    /// </remarks>
    Task<bool> InsertAsync(FocusedField target, string text, CancellationToken ct);
}
