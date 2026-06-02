namespace Cotabby.Core.Focus;

/// <summary>
/// A snapshot of the editable field currently holding focus, captured at a single
/// instant. Values are eventually-consistent; callers must treat a snapshot as a
/// stale read by the time they act on it and re-resolve before mutating state.
/// </summary>
/// <remarks>
/// Mirrors the macOS port's <c>FocusSnapshot</c>. The <see cref="ElementHandle"/>
/// is an opaque token so Core does not depend on UIAutomationElement.
/// </remarks>
public sealed record FocusedField
{
    /// <summary>Opaque, platform-specific element identity (UIA cache request, AX ref, …).</summary>
    public required object ElementHandle { get; init; }

    /// <summary>Process id of the host app. Used for app-specific behavior toggles.</summary>
    public required int ProcessId { get; init; }

    /// <summary>Best-effort host process name without extension (e.g. <c>"chrome"</c>).</summary>
    public required string ProcessName { get; init; }

    /// <summary>Full text content of the field at snapshot time. May be empty.</summary>
    public required string Text { get; init; }

    /// <summary>Caret position as a UTF-16 offset into <see cref="Text"/>.</summary>
    public required int CaretOffset { get; init; }

    /// <summary>Caret screen rectangle in physical pixels (DPI-aware).</summary>
    public required ScreenRect CaretRect { get; init; }

    /// <summary>Field bounding rectangle in physical pixels.</summary>
    public required ScreenRect FieldRect { get; init; }

    /// <summary>True if the field is single-line (input, search box, etc.).</summary>
    public required bool IsSingleLine { get; init; }

    /// <summary>True if the field is a password field — suggestions must be suppressed.</summary>
    public required bool IsSecure { get; init; }
}

/// <summary>Screen rectangle in physical pixels.</summary>
public readonly record struct ScreenRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public static ScreenRect Empty => default;
    public bool IsEmpty => Width <= 0 || Height <= 0;
}
