using Cotabby.Core.Focus;

namespace Cotabby.Core.Overlay;

/// <summary>
/// Surface that displays ghost text near the caret. Core only writes to it, so the coordinator
/// can be tested without a real window.
/// </summary>
public interface IOverlayPresenter
{
    /// <summary>
    /// Show ghost text at the given screen rect. Replaces any visible text. The field the anchor was
    /// read from, when given, lets the ghost take the host's font.
    /// </summary>
    void Show(string text, ScreenRect anchor, OverlayStyle style = OverlayStyle.Inline, FocusedField? field = null);

    /// <summary>Update the visible text in place without moving the anchor.</summary>
    void Update(string text);

    /// <summary>Hide the overlay if visible.</summary>
    void Hide();
}
