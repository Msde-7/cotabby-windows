namespace Cotabby.Core.Focus;

/// <summary>
/// A snapshot of the editable field holding focus, captured at one instant. A snapshot can be
/// stale by the time a caller acts on it, so read the field again before changing its text.
/// </summary>
public sealed record FocusedField
{
    /// <summary>Process id of the host app. Used for app-specific behavior toggles.</summary>
    public required int ProcessId { get; init; }

    /// <summary>Best-effort host process name without extension (e.g. <c>"chrome"</c>).</summary>
    public required string ProcessName { get; init; }

    /// <summary>Full text content of the field at snapshot time. May be empty.</summary>
    public required string Text { get; init; }

    /// <summary>Caret position as a UTF-16 offset into <see cref="Text"/>.</summary>
    public required int CaretOffset { get; init; }

    /// <summary>The text before the caret. A caret offset outside the text is clamped.</summary>
    public string PrecedingText => Text[..Math.Clamp(CaretOffset, 0, Text.Length)];

    /// <summary>The text after the caret.</summary>
    public string TrailingText => Text[Math.Clamp(CaretOffset, 0, Text.Length)..];

    /// <summary>Caret screen rectangle in physical pixels (DPI-aware).</summary>
    public required ScreenRect CaretRect { get; init; }

    /// <summary>True when the host gave no caret rectangle and <see cref="CaretRect"/> is the field's own.</summary>
    public bool CaretIsEstimated { get; init; }

    /// <summary>Field bounding rectangle in physical pixels.</summary>
    public required ScreenRect FieldRect { get; init; }

    /// <summary>True if the field is single-line (input, search box, etc.).</summary>
    public required bool IsSingleLine { get; init; }

    /// <summary>True if the field is a password field, where suggestions must be suppressed.</summary>
    public required bool IsSecure { get; init; }

    /// <summary>True when a non-empty range of text is selected.</summary>
    public bool HasSelection { get; init; }

    /// <summary>Accessible name of the field, usually its visible label or placeholder.</summary>
    public string Label { get; init; } = "";

    /// <summary>Help text or placeholder the host exposes for the field.</summary>
    public string HelpText { get; init; } = "";

    /// <summary>Automation id. In Chromium hosts this is the DOM id.</summary>
    public string AutomationId { get; init; } = "";

    /// <summary>Class name. In Chromium hosts this is the DOM class list.</summary>
    public string ClassName { get; init; } = "";

    /// <summary>Title of the window that holds the field.</summary>
    public string WindowTitle { get; init; } = "";
}

/// <summary>Screen rectangle in physical pixels.</summary>
public readonly record struct ScreenRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public static ScreenRect Empty => default;
    public bool IsEmpty => Width <= 0 || Height <= 0;
}
