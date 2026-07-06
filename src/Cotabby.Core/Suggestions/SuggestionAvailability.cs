using System.Collections.Generic;
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
    /// <param name="blockedApps">
    /// Optional case-insensitive set of process names where suggestions are
    /// suppressed. Mirrors the macOS per-app blocklist. Pass null to disable
    /// the check entirely.
    /// </param>
    public static bool ShouldRequest(
        FocusedField? field,
        KeyboardEvent ev,
        ISet<string>? blockedApps = null)
    {
        if (field is null) return false;
        if (field.IsSecure) return false;
        // Never suggest into our own windows (settings, welcome wizard,
        // emoji popup) — ghost text over Cotabby's own text boxes is both
        // useless and confusing. The macOS app has the same self-exclusion.
        if (field.ProcessId == Environment.ProcessId) return false;
        // Upstream gate: at least one non-whitespace character in the field.
        if (string.IsNullOrWhiteSpace(field.Text)) return false;
        if (ev.HasNonShiftModifier) return false;
        if (!ev.IsKeyDown) return false;
        if (blockedApps is not null
            && !string.IsNullOrEmpty(field.ProcessName)
            && blockedApps.Contains(field.ProcessName))
        {
            return false;
        }
        // Printable characters trigger; backspace shrinks an active session but
        // does not start a new one, and arrow/escape/enter cancel.
        return ev.Kind == KeyKind.Character;
    }

    /// <summary>True if a key event should cancel any in-flight or visible suggestion.</summary>
    public static bool ShouldCancel(KeyboardEvent ev) =>
        ev.IsKeyDown && ev.Kind is KeyKind.Escape or KeyKind.Enter or KeyKind.Arrow or KeyKind.Delete;
}
