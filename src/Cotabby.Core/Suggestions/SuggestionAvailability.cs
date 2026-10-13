using Cotabby.Core.Focus;
using Cotabby.Core.Input;

namespace Cotabby.Core.Suggestions;

/// <summary>
/// Pure gating rules that decide whether Cotabby may react to the current focus.
/// Mirrors the macOS <c>SuggestionAvailabilityEvaluator</c>.
/// </summary>
public static class SuggestionAvailability
{
    /// <summary>Why suggestions are off everywhere right now, or null when they are allowed.</summary>
    public static string? DisabledReason(SuggestionSettings settings, DateTimeOffset now, bool batterySaverOn)
    {
        if (!settings.Enabled) return "Cotabby is turned off.";
        if (settings.Pause?.IsActive(now) == true) return "Cotabby is temporarily paused.";
        if (batterySaverOn && settings.PauseOnBatterySaver) return "Cotabby is paused because battery saver is on.";
        return null;
    }

    /// <summary>Why suggestions are off for this field, or null when they are allowed.</summary>
    public static string? DisabledReason(SuggestionSettings settings, FocusedField field)
    {
        // Ghost text over Cotabby's own text boxes is useless and confusing.
        if (field.ProcessId == Environment.ProcessId) return "Cotabby does not suggest in its own windows.";
        if (settings.BlockedApps.Contains(field.ProcessName)) return $"Cotabby is disabled in {field.ProcessName}.";
        if (TerminalAppDetector.IsTerminal(field.ProcessName)) return "Cotabby is not available in terminal apps.";
        if (!settings.SuggestInIntegratedTerminals && TerminalAppDetector.IsIntegratedTerminal(field.ClassName))
        {
            return "Cotabby is not available in the integrated terminal.";
        }

        if (field.IsSecure) return "Secure fields are left alone.";
        if (CredentialFieldDetector.IsCredentialField(field)) return CredentialFieldDetector.BlockedReason;
        if (field.HasSelection) return "Text is selected.";
        return null;
    }

    /// <summary>True when a key press may start a fresh prediction.</summary>
    public static bool SchedulesPrediction(KeyboardEvent ev) =>
        ev.IsKeyDown && !ev.HasNonShiftModifier
        && ev.Kind is KeyKind.Character or KeyKind.Backspace or KeyKind.Enter;

    /// <summary>True when a key press invalidates a visible suggestion that it does not type through.</summary>
    public static bool ClearsSuggestion(KeyboardEvent ev) =>
        ev.IsKeyDown
        && ev.Kind is KeyKind.Escape or KeyKind.Enter or KeyKind.Arrow or KeyKind.Delete or KeyKind.Backspace;

    /// <summary>At least one non-whitespace character must precede the caret.</summary>
    public static bool HasTextToContinue(FocusedField field) => !string.IsNullOrWhiteSpace(field.PrecedingText);
}
