using Cotabby.Core.Focus;
using Cotabby.Core.Input;

namespace Cotabby.Core.Suggestions;

/// <summary>
/// Pure gating logic deciding whether a suggestion may be requested for a given
/// (field, key) pair. Mirrors the macOS port's <c>SuggestionAvailabilityEvaluator</c>.
/// </summary>
public static class SuggestionAvailability
{
    /// <summary>
    /// Returns true if the coordinator should attempt to fetch a suggestion in
    /// response to <paramref name="ev"/> on <paramref name="field"/>. Returning
    /// false means "don't even ask the engine."
    /// </summary>
    public static bool ShouldRequest(FocusedField? field, KeyboardEvent ev)
    {
        if (field is null) return false;
        if (field.IsSecure) return false;
        if (field.Text.Length == 0) return false; // need at least some prefix
        if (ev.HasNonShiftModifier) return false;
        if (!ev.IsKeyDown) return false;
        // Printable characters trigger; backspace shrinks an active session but
        // does not start a new one, and arrow/escape/enter cancel.
        return ev.Kind == KeyKind.Character;
    }

    /// <summary>True if a key event should cancel any in-flight or visible suggestion.</summary>
    public static bool ShouldCancel(KeyboardEvent ev) =>
        ev.IsKeyDown && ev.Kind is KeyKind.Escape or KeyKind.Enter or KeyKind.Arrow or KeyKind.Delete;
}
