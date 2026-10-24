namespace Cotabby.Core.Overlay;

/// <summary>
/// Whether a newly shown ghost fades in. A fade only plays when the overlay appears, never when it
/// updates in place, and never when the system asks apps to cut animation. Mirrors the macOS
/// <c>SuggestionFadeInPolicy</c>.
/// </summary>
public static class SuggestionFadeInPolicy
{
    /// <param name="isEnabled">The user's Fade In Suggestions setting.</param>
    /// <param name="overlayWasVisible">Whether the overlay was on screen before this show.</param>
    /// <param name="reduceMotion">True when Windows animation effects are turned off.</param>
    public static bool ShouldFadeIn(bool isEnabled, bool overlayWasVisible, bool reduceMotion) =>
        isEnabled && !reduceMotion && !overlayWasVisible;
}
