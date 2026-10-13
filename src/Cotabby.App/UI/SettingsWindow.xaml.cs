using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Cotabby.App.Hosting;
using Cotabby.App.UI.Controls;
using Cotabby.App.UI.Settings;

namespace Cotabby.App.UI;

/// <summary>
/// The settings window. A sidebar picks a pane and the pane shows cards of rows. There is
/// no Save button. Every control applies and saves its change at once, and the window
/// refreshes whenever settings change elsewhere, such as from the tray menu.
/// </summary>
public partial class SettingsWindow : Window
{
    private static SettingsWindow? _instance;

    private readonly AppHost _host;
    private readonly SettingsContext _context;
    private readonly List<PaneView> _panes = [];
    private readonly ScrollViewer _results = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

    public SettingsWindow(AppHost host)
    {
        _host = host;
        InitializeComponent();
        WindowTheme.Follow(this);

        _context = new SettingsContext(host, Navigate);
        foreach (var group in SettingsPanes.Build(_context))
        {
            for (int i = 0; i < group.Count; i++)
            {
                _panes.Add(new PaneView(group[i], _context));
                Detail.Children.Add(_panes[^1]);
                NavList.Items.Add(NavItem(group[i], startsGroup: i == 0 && NavList.Items.Count > 0));
            }
        }
        Detail.Children.Add(_results);

        host.SettingsApplied += _context.Refresh;
        // A model can finish loading from the tray while the window is in the background.
        Activated += (_, _) => _context.Refresh();
        NavList.SelectedIndex = 0;
    }

    public static void ShowOrFocus(AppHost host)
    {
        if (_instance is null)
        {
            _instance = new SettingsWindow(host);
            _instance.Show();
        }
        _instance.Activate();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control) SearchBox.Focus();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        // A text box that still has focus commits its edit when focus leaves it.
        Keyboard.ClearFocus();
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _host.SettingsApplied -= _context.Refresh;
        _context.Close();
        _instance = null;
    }

    private static ListBoxItem NavItem(SettingsPane pane, bool startsGroup)
    {
        var icon = Ui.Icon(pane.Glyph, 14).Themed(TextBlock.ForegroundProperty, "AccentBrush");
        icon.Width = 22;
        var label = Ui.Text(pane.Title);
        label.Margin = new Thickness(8, 0, 0, 0);
        return new ListBoxItem
        {
            Content = new StackPanel { Orientation = Orientation.Horizontal, Children = { icon, label } },
            // Extra space above the first item of a group is what separates the groups.
            Margin = new Thickness(0, startsGroup ? 10 : 0, 0, 0),
        };
    }

    private void Navigate(SettingsPane pane) => NavList.SelectedIndex = _panes.FindIndex(view => view.Pane == pane);

    private void OnNavChanged(object sender, SelectionChangedEventArgs e) => ShowDetail();

    private void OnSearchChanged(object sender, TextChangedEventArgs e) => ShowDetail();

    /// <summary>Shows the search results while there is a query, and the selected pane otherwise.</summary>
    private void ShowDetail()
    {
        var query = SearchBox.Text.Trim();
        bool searching = query.Length > 0;
        if (searching) _results.Content = Results(query);
        _results.Visibility = Shown(searching);
        for (int i = 0; i < _panes.Count; i++) _panes[i].Visibility = Shown(!searching && i == NavList.SelectedIndex);
    }

    private StackPanel Results(string query)
    {
        var list = new StackPanel { Margin = new Thickness(28, 22, 28, 28), MaxWidth = 660 };
        var matches = _panes
            .SelectMany(view => view.Pane.Cards.SelectMany(card => card.Rows).Select(row => (view.Pane, Row: row)))
            .Where(match => match.Row.Matches(query))
            // Title matches read as more relevant than a hit in the description or keywords.
            .OrderByDescending(match => match.Row.Title.Contains(query, StringComparison.OrdinalIgnoreCase));
        foreach (var (pane, row) in matches) list.Children.Add(Result(pane, row));
        if (list.Children.Count == 0) list.Children.Add(Ui.Text($"No settings match \u201C{query}\u201D.", "Caption"));
        return list;
    }

    private Border Result(SettingsPane pane, SettingsRow row)
    {
        var paneName = Ui.Text(pane.Title, "Caption");
        paneName.VerticalAlignment = VerticalAlignment.Center;
        paneName.Margin = new Thickness(12, 0, 0, 0);
        DockPanel.SetDock(paneName, Dock.Right);

        var result = new Border
        {
            Padding = new Thickness(14, 10, 14, 10),
            Margin = new Thickness(0, 0, 0, 8),
            Cursor = Cursors.Hand,
            Child = new DockPanel
            {
                Children = { paneName, new StackPanel { Children = { Ui.Text(row.Title), Ui.Text(row.Subtitle, "Caption") } } },
            },
        }.Styled("Card");
        result.MouseLeftButtonUp += (_, _) =>
        {
            SearchBox.Text = "";
            Navigate(pane);
            // The row can only scroll into view once its pane has been laid out.
            Dispatcher.BeginInvoke(() => row.View.BringIntoView(), DispatcherPriority.Loaded);
        };
        return result;
    }

    private static Visibility Shown(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;
}
