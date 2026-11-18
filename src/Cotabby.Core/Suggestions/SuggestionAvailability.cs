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

    /// <summary>
    /// True when a key press may start a fresh prediction. As upstream, that is typing, Backspace
    /// and Delete with any modifier, and the shortcuts that edit text. Enter is a dismissal: it
    /// often sends or submits rather than starting a line, and the next key asks again anyway.
    /// </summary>
    public static bool SchedulesPrediction(KeyboardEvent ev) =>
        ev.IsKeyDown
        && ((ev.Kind == KeyKind.Character && !ev.HasNonShiftModifier)
            || ev.Kind is KeyKind.Backspace or KeyKind.Delete
            || IsEditingShortcut(ev));

    /// <summary>
    /// Select all, undo, redo, cut and paste: the shortcuts upstream predicts after. Every other
    /// shortcut, such as save or copy, only clears the suggestion.
    /// </summary>
    public static bool IsEditingShortcut(KeyboardEvent ev) =>
        ev.Kind == KeyKind.Shortcut
        && ((ev.Modifiers & (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Win)) == KeyModifiers.Control
                && ev.VirtualKey is 'A' or 'Z' or 'Y' or 'X' or 'V'
            || (ev.Modifiers == KeyModifiers.Shift && ev.VirtualKey == KeyClassifier.VkInsert));

    /// <summary>True for a key that turns down a visible suggestion, which is then not offered again for the same text.</summary>
    public static bool Dismisses(KeyboardEvent ev) =>
        ev.IsKeyDown
        && (ev.Kind is KeyKind.Escape or KeyKind.Enter || (ev.Kind == KeyKind.Shortcut && !IsEditingShortcut(ev)));

    /// <summary>True when a key press invalidates a visible suggestion that it does not type through.</summary>
    public static bool ClearsSuggestion(KeyboardEvent ev) =>
        ev.IsKeyDown
        && ev.Kind is KeyKind.Escape or KeyKind.Enter or KeyKind.Arrow or KeyKind.Delete or KeyKind.Backspace
            or KeyKind.Shortcut;

    /// <summary>At least one non-whitespace character must precede the caret.</summary>
    public static bool HasTextToContinue(FocusedField field) => !string.IsNullOrWhiteSpace(field.PrecedingText);
}
