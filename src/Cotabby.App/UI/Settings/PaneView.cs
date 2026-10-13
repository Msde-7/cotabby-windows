using System.Windows;
using System.Windows.Controls;
using Cotabby.App.UI.Controls;

namespace Cotabby.App.UI.Settings;

/// <summary>Renders a pane as a scrolling column of cards.</summary>
internal sealed class PaneView : ScrollViewer
{
    public PaneView(SettingsPane pane, SettingsContext context)
    {
        Pane = pane;
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;

        var title = Ui.Text(pane.Title);
        title.FontSize = 20;
        title.FontWeight = FontWeights.SemiBold;
        title.Margin = new Thickness(2, 0, 0, 16);

        var column = new StackPanel { Margin = new Thickness(28, 22, 28, 8), MaxWidth = 660, Children = { title } };
        foreach (var card in pane.Cards) column.Children.Add(CardView(card, context));
        Content = column;
    }

    public SettingsPane Pane { get; }

    private static StackPanel CardView(SettingsCard card, SettingsContext context)
    {
        var rows = new StackPanel();
        var separators = new List<Border>();
        foreach (var row in card.Rows)
        {
            separators.Add(Ui.Separator());
            rows.Children.Add(separators[^1]);
            rows.Children.Add(row.View);
        }

        var view = new StackPanel();
        if (card.Header is { } header) view.Children.Add(Ui.Text(header, "CardHeader"));
        view.Children.Add(new Border { Padding = new Thickness(14, 3, 14, 3), Child = rows }.Styled("Card"));

        context.OnRefresh(() =>
        {
            var settings = context.Settings;
            view.Visibility = Shown(card.Visible?.Invoke(settings) != false);
            bool anyAbove = false;
            for (int i = 0; i < card.Rows.Length; i++)
            {
                var row = card.Rows[i];
                bool visible = row.Visible?.Invoke(settings) != false;
                row.View.Visibility = Shown(visible);
                row.View.IsEnabled = row.Enabled?.Invoke(settings) != false;
                separators[i].Visibility = Shown(visible && anyAbove);
                anyAbove |= visible;
            }
        });
        return view;
    }

    private static Visibility Shown(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;
}
