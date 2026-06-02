using Cotabby.Core.Focus;

namespace Cotabby.Core.Overlay;

/// <summary>
/// Surface that displays ghost text near the caret. The WPF implementation owns
/// the transparent topmost window; Core treats it as a write-only sink so the
/// coordinator can be unit-tested without spinning up a real window.
/// </summary>
public interface IOverlayPresenter
{
    /// <summary>Show ghost text at the given screen rect. Replaces any visible text.</summary>
    void Show(string text, ScreenRect anchor);

    /// <summary>Update the visible text in place without moving the anchor — used as tokens stream.</summary>
    void Update(string text);

    /// <summary>Hide the overlay if visible.</summary>
    void Hide();
}
