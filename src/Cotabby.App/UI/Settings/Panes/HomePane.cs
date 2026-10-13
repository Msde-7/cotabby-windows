using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Cotabby.App.Hosting;
using Cotabby.App.Settings;
using Cotabby.App.UI.Controls;
using Cotabby.Core.Macros;

namespace Cotabby.App.UI.Settings.Panes;

internal static class HomePane
{
    public static SettingsPane Build(Rows r, IReadOnlyList<SettingsPane> otherPanes)
    {
        var context = r.Context;
        var status = new StatusLine();
        var words = Ui.Text("", "Mono");
        context.OnRefresh(() =>
        {
            var (ready, text) = Status(context.Host);
            status.Set(ready, text);
            words.Text = $"{context.Host.Coordinator.Metrics.WordsAccepted:N0}";
        });

        return new SettingsPane("Home", "\uE80F", "Overview and status",
            new SettingsCard(null, Rows.Custom(Branding.Identity("Write faster, everywhere you type."))),
            new SettingsCard("Status",
                r.Labeled("Cotabby", "Suggests the next words as you type in any app. Press your accept key to take them.", status),
                r.Labeled("Words Completed", "Words you have accepted from suggestions so far.", words)),
            new SettingsCard("Quick Settings", Rows.Custom(QuickLinks(context, otherPanes))),
            new SettingsCard("Macros", Rows.Custom(MacroReference())));
    }

    /// <summary>Whether suggestions can appear right now, and why not when they cannot.</summary>
    private static (bool Ready, string Text) Status(AppHost host)
    {
        var settings = host.Settings;
        if (!settings.Enabled) return (false, "Cotabby is off");
        if (settings.Pause?.StatusText(DateTimeOffset.Now) is { } paused) return (false, paused);
        if (settings.Engine == EngineKind.Endpoint)
        {
            return settings.EndpointModel.Length > 0
                ? (true, $"Ready · {settings.EndpointModel}")
                : (false, "No endpoint model selected");
        }
        return host.Runtime is { IsReady: true, ActiveModel: { } model }
            ? (true, $"Ready · {model.DisplayName}")
            : (false, "No model loaded");
    }

    private static UniformGrid QuickLinks(SettingsContext context, IReadOnlyList<SettingsPane> panes)
    {
        var grid = new UniformGrid { Columns = 2, Margin = new Thickness(-4, 8, -4, 8) };
        foreach (var pane in panes)
        {
            var icon = Ui.Icon(pane.Glyph, 16).Themed(TextBlock.ForegroundProperty, "AccentBrush");
            icon.Width = 28;
            var text = new StackPanel { Children = { Ui.Text(pane.Title), Ui.Text(pane.Summary, "Caption") } };
            var link = new Border
            {
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10, 8, 10, 8),
                Margin = new Thickness(4),
                Cursor = Cursors.Hand,
                Child = new DockPanel { Children = { icon, text } },
            }.Themed(Border.BackgroundProperty, "InputBg");
            link.MouseLeftButtonUp += (_, _) => context.Navigate(pane);
            grid.Children.Add(link);
        }
        return grid;
    }

    /// <summary>A collapsed list of every macro family. It is built the first time it is opened.</summary>
    private static StackPanel MacroReference()
    {
        var chevron = Ui.Icon("\uE76C", 10);
        var header = new DockPanel
        {
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            MinHeight = 44,
            Children = { chevron, Ui.Text("All macros") },
        };
        chevron.Margin = new Thickness(2, 0, 10, 0);
        var body = new StackPanel { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 10) };

        header.MouseLeftButtonUp += (_, _) =>
        {
            bool open = body.Visibility != Visibility.Visible;
            if (open && body.Children.Count == 0) FillMacroReference(body);
            body.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            chevron.Text = open ? "\uE70D" : "\uE76C";
        };
        return new StackPanel { Children = { header, body } };
    }

    private static void FillMacroReference(StackPanel body)
    {
        foreach (var group in MacroReferenceCatalog.Build(MacroEngine.Standard()))
        {
            var title = new TextBlock { Text = group.Title, FontSize = 13, FontWeight = FontWeights.SemiBold };
            title.Margin = new Thickness(0, 10, 0, 2);
            body.Children.Add(title);
            body.Children.Add(Ui.Text(group.Summary, "Caption"));
            foreach (var example in group.Examples)
            {
                var input = Ui.Text("/" + example.Input, "Mono");
                input.Width = 190;
                var line = Ui.Across(input, Ui.Text(example.Result, "Mono").Themed(TextBlock.ForegroundProperty, "TextSecondary"));
                line.Margin = new Thickness(0, 5, 0, 0);
                body.Children.Add(line);
            }
        }
    }
}
