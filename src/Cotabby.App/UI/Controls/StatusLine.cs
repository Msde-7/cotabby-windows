using System.Windows;
using System.Windows.Controls;
using System.Windows.Shapes;

namespace Cotabby.App.UI.Controls;

/// <summary>A colored dot with a short status text. Green means ready, orange means not.</summary>
internal sealed class StatusLine : StackPanel
{
    private readonly Ellipse _dot = new() { Width = 9, Height = 9, Margin = new Thickness(0, 1, 7, 0) };
    private readonly TextBlock _text = Ui.Text("").Themed(TextBlock.ForegroundProperty, "TextSecondary");

    public StatusLine()
    {
        Orientation = Orientation.Horizontal;
        _dot.VerticalAlignment = VerticalAlignment.Center;
        _text.TextTrimming = TextTrimming.CharacterEllipsis;
        _text.MaxWidth = 300;
        Children.Add(_dot);
        Children.Add(_text);
    }

    public void Set(bool ready, string text)
    {
        _dot.SetResourceReference(Shape.FillProperty, ready ? "GreenBrush" : "OrangeBrush");
        _text.Text = text;
    }
}
