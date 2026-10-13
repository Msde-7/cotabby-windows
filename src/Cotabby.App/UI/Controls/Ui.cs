using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Cotabby.App.UI.Controls;

/// <summary>Small factories for elements styled by Theme.xaml.</summary>
internal static class Ui
{
    private static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    private static readonly Uri LogoUri = new("pack://application:,,,/Cotabby;component/Assets/CotabbyLogo.png");

    /// <summary>Points the style at a Theme.xaml key. It resolves once the element joins a window.</summary>
    public static T Styled<T>(this T element, string styleKey) where T : FrameworkElement
    {
        element.SetResourceReference(FrameworkElement.StyleProperty, styleKey);
        return element;
    }

    /// <summary>Binds a brush property to a theme brush so it follows light and dark.</summary>
    public static T Themed<T>(this T element, DependencyProperty property, string brushKey) where T : FrameworkElement
    {
        element.SetResourceReference(property, brushKey);
        return element;
    }

    public static TextBlock Text(string text, string styleKey = "RowLabel") =>
        new TextBlock { Text = text }.Styled(styleKey);

    public static TextBlock Icon(string glyph, double size) => new()
    {
        Text = glyph,
        FontFamily = IconFont,
        FontSize = size,
        VerticalAlignment = VerticalAlignment.Center,
    };

    public static Button Button(string label, Action onClick, string styleKey = "Btn")
    {
        var button = new Button { Content = label }.Styled(styleKey);
        button.Click += (_, _) => onClick();
        return button;
    }

    public static Border Separator() => new Border().Styled("RowSep");

    public static Border Logo(double size) => new()
    {
        Width = size,
        Height = size,
        CornerRadius = new CornerRadius(size * 0.22),
        Background = new ImageBrush(new BitmapImage(LogoUri)) { Stretch = Stretch.UniformToFill },
    };

    /// <summary>Lays children out in a row with a gap between them. Replaces their margins.</summary>
    public static StackPanel Across(params FrameworkElement[] children)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var child in children)
        {
            child.Margin = new Thickness(panel.Children.Count == 0 ? 0 : 8, 0, 0, 0);
            child.VerticalAlignment = VerticalAlignment.Center;
            panel.Children.Add(child);
        }
        return panel;
    }

    /// <summary>A text box with an Add button. Enter or the button submits the trimmed text.</summary>
    public static FrameworkElement AddBox(string placeholder, Action<string> add)
    {
        var box = new TextBox { Tag = placeholder, Width = 260 }.Styled("Input");

        void Submit()
        {
            var text = box.Text.Trim();
            box.Text = "";
            if (text.Length > 0) add(text);
        }

        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) Submit();
        };
        return Across(box, Button("Add", Submit));
    }

    /// <summary>Calls back when the box loses focus or, for a single line box, on Enter.</summary>
    public static void OnCommit(this TextBox box, Action<string> commit)
    {
        box.LostKeyboardFocus += (_, _) => commit(box.Text);
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && !box.AcceptsReturn) commit(box.Text);
        };
    }

    /// <summary>Opens a file or folder in Explorer, which also offers an app for unknown file types.</summary>
    public static void OpenPath(string path) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });

    public static void OpenUrl(string url) =>
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
}
