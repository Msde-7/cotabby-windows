using Cotabby.Core.Focus;

namespace Cotabby.Core.Overlay;

/// <summary>
/// The horizontal band wrapped ghost rows may take, from what the host exposed. Ported from the
/// macOS <c>GhostWrapBandPolicy</c>.
/// </summary>
/// <remarks>
/// Left edge: the host's caret line box when known, since that is where its next line starts, else
/// the field's inner edge. Right edge: the field's frame minus the same inset the left edge showed,
/// because editors pad both sides of their text alike. Rows never leave the work area. All values
/// are in one unit; the overlay passes physical pixels with the scale applied to the insets.
/// </remarks>
public static class GhostWrapBandPolicy
{
    public readonly record struct Input(
        ScreenRect Caret, ScreenRect Field, double? LineLeft, double WorkLeft, double WorkRight, double Scale = 1);

    /// <summary>Padding assumed between a field's frame and its text when no line box says otherwise.</summary>
    public const double DefaultContentInset = 4;

    /// <summary>A line box this far inside its field is still taken as the field's padding.</summary>
    public const double MaximumBelievableInset = 60;

    public const double ScreenMargin = 8;

    /// <summary>Fields narrower than this are not text containers worth wrapping inside.</summary>
    public const double MinimumContainerWidth = 40;

    public static (double Left, double Right) Band(Input input)
    {
        double scale = input.Scale > 0 ? input.Scale : 1;
        double screenLeft = input.WorkLeft + ScreenMargin * scale;
        double screenRight = input.WorkRight - ScreenMargin * scale;
        double? left = input.LineLeft;
        double? right = null;
        var field = input.Field;
        if (!field.IsEmpty && field.Width > MinimumContainerWidth * scale)
        {
            double inset = ContentInset(input.LineLeft, field.X, scale);
            // A line box left of the field is not this field's line; the field's own edge stands.
            if (left is { } lineLeft && lineLeft < field.X - scale) left = null;
            left ??= field.X + inset;
            right = field.Right - inset;
        }
        double bandLeft = Math.Min(Math.Max(left ?? input.Caret.X, screenLeft), screenRight);
        double bandRight = Math.Max(Math.Min(right ?? screenRight, screenRight), bandLeft + 1);
        return (bandLeft, bandRight);
    }

    private static double ContentInset(double? lineLeft, double fieldLeft, double scale)
    {
        if (lineLeft is not { } measuredLeft) return DefaultContentInset * scale;
        double measured = measuredLeft - fieldLeft;
        return measured >= 0 && measured <= MaximumBelievableInset * scale ? measured : DefaultContentInset * scale;
    }
}
