using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Cotabby.App.UI.Controls;

namespace Cotabby.App.UI.Settings.Panes;

/// <summary>The logo, name and version block shared by the Home and About panes.</summary>
internal static class Branding
{
    /// <summary>The informational version without the commit suffix the SDK appends.</summary>
    private static string Version { get; } =
        typeof(Branding).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion.Split('+')[0] ?? "";

    public static FrameworkElement Identity(string tagline)
    {
        var name = Ui.Text("Cotabby");
        name.FontSize = 20;
        name.FontWeight = FontWeights.SemiBold;
        var version = Ui.Text($"Version {Version}", "Caption");
        version.Margin = new Thickness(10, 3, 0, 0);
        version.VerticalAlignment = VerticalAlignment.Center;

        var text = new StackPanel
        {
            Margin = new Thickness(14, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new StackPanel { Orientation = Orientation.Horizontal, Children = { name, version } },
                Ui.Text(tagline, "Caption"),
            },
        };
        return new DockPanel { Margin = new Thickness(0, 12, 0, 12), Children = { Ui.Logo(52), text } };
    }
}
