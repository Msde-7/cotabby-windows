using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Cotabby.App.UI.Controls;

/// <summary>Pill shaped tags for lists the user edits in place.</summary>
internal static class Chips
{
    private static readonly Thickness Gap = new(0, 0, 6, 6);

    /// <summary>A chip for a chosen value. A null <paramref name="remove"/> leaves out the close button.</summary>
    public static FrameworkElement Removable(string text, Action? remove)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(new TextBlock { Text = text, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        if (remove is not null)
        {
            var close = Ui.Icon("\uE711", 9).Themed(TextBlock.ForegroundProperty, "TextSecondary");
            close.Margin = new Thickness(7, 1, 0, 0);
            close.Cursor = Cursors.Hand;
            close.ToolTip = "Remove";
            close.MouseLeftButtonUp += (_, _) => remove();
            content.Children.Add(close);
        }
        return Pill(content).Themed(Border.BackgroundProperty, "KeycapBg").Themed(Border.BorderBrushProperty, "CardBorder");
    }

    /// <summary>A dashed chip that offers a value to add.</summary>
    public static FrameworkElement Addable(string text, Action add)
    {
        // A Border cannot dash its stroke, so the outline is a Rectangle.
        var outline = new Rectangle
        {
            RadiusX = 12,
            RadiusY = 12,
            StrokeThickness = 1,
            StrokeDashArray = [3, 2],
        }.Themed(Shape.StrokeProperty, "TextSecondary");
        var label = new TextBlock { Text = "+ " + text, FontSize = 12, Margin = new Thickness(10, 4, 10, 4) }
            .Themed(TextBlock.ForegroundProperty, "TextSecondary");
        var cell = new Grid { Margin = Gap, Cursor = Cursors.Hand, Background = Brushes.Transparent, Children = { outline, label } };
        cell.MouseLeftButtonUp += (_, _) => add();
        return cell;
    }

    /// <summary>A chip that is either on or off.</summary>
    public static FrameworkElement Checkable(string text, bool isChecked, Action toggle)
    {
        var label = new TextBlock { Text = text, FontSize = 12 };
        var chip = Pill(label);
        chip.Cursor = Cursors.Hand;
        chip.Themed(Border.BackgroundProperty, isChecked ? "AccentTint" : "KeycapBg")
            .Themed(Border.BorderBrushProperty, isChecked ? "AccentBrush" : "CardBorder");
        if (isChecked) label.FontWeight = FontWeights.SemiBold;
        chip.MouseLeftButtonUp += (_, _) => toggle();
        return chip;
    }

    private static Border Pill(UIElement content) => new()
    {
        CornerRadius = new CornerRadius(12),
        BorderThickness = new Thickness(1),
        Padding = new Thickness(10, 4, 10, 4),
        Margin = Gap,
        Child = content,
    };
}
