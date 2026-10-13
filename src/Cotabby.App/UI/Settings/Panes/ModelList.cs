using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Cotabby.App.UI.Controls;
using Cotabby.Core.Models;
using Cotabby.Inference;

namespace Cotabby.App.UI.Settings.Panes;

/// <summary>
/// The downloadable models with their download, load and active state. Clicking a model
/// that is on disk switches to it. One that is not needs its Download button, so a stray
/// click cannot start a download of several gigabytes.
/// </summary>
internal sealed class ModelList : StackPanel
{
    private readonly SettingsContext _context;
    private CancellationTokenSource? _loading;
    private ModelDownloader.Progress? _progress;
    private string? _error;
    private TextBlock? _progressLabel;
    private ProgressBar? _progressBar;

    public ModelList(SettingsContext context)
    {
        _context = context;
        context.Closed += () => _loading?.Cancel();
        context.OnRefresh(Rebuild);
    }

    private bool Busy => _loading is not null;

    private void Rebuild()
    {
        Children.Clear();
        _progressLabel = null;
        _progressBar = null;

        var selectedId = _context.Settings.ActiveModelId ?? ModelCatalog.All[0].Id;
        foreach (var model in ModelCatalog.Available())
        {
            if (Children.Count > 0) Children.Add(Ui.Separator());
            Children.Add(Row(model, model.Id == selectedId));
        }
        if (_error is not null)
        {
            var error = Ui.Text("Could not prepare the model. " + _error, "Caption")
                .Themed(TextBlock.ForegroundProperty, "OrangeBrush");
            error.Margin = new Thickness(0, 4, 0, 10);
            Children.Add(error);
        }
        ShowProgress();
    }

    private Border Row(CotabbyModel model, bool selected)
    {
        var runtime = _context.Host.Runtime;
        bool loadingThis = Busy && selected;

        var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        names.Children.Add(new TextBlock { Text = model.DisplayName, FontSize = 13, FontWeight = FontWeights.SemiBold });
        var subtitle = Ui.Text(model.Subtitle, "Caption");
        subtitle.Margin = new Thickness(0, 2, 0, 0);
        names.Children.Add(subtitle);
        Grid.SetColumn(names, 1);

        FrameworkElement trailing;
        if (loadingThis)
        {
            trailing = _progressLabel = Ui.Text("", "Caption");
        }
        else if (selected && runtime.IsReady && runtime.ActiveModel?.Id == model.Id)
        {
            var check = Ui.Icon("\uE73E", 12);
            var active = new TextBlock { Text = "Active", FontSize = 12, FontWeight = FontWeights.SemiBold };
            check.Themed(TextBlock.ForegroundProperty, "GreenBrush");
            active.Themed(TextBlock.ForegroundProperty, "GreenBrush");
            trailing = Ui.Across(check, active);
        }
        else if (!ModelDownloader.IsCached(model))
        {
            trailing = Ui.Button("Download", () => Load(model));
        }
        else if (selected)
        {
            trailing = Ui.Button("Load", () => Load(model));
        }
        else
        {
            trailing = Ui.Text("Downloaded", "Caption");
        }
        trailing.VerticalAlignment = VerticalAlignment.Center;
        trailing.IsEnabled = !Busy;
        Grid.SetColumn(trailing, 2);

        var grid = new Grid { Margin = new Thickness(0, 10, 0, 10), Children = { SelectionMark(selected), names, trailing } };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition());
        if (loadingThis)
        {
            _progressBar = new ProgressBar { Maximum = 1, Margin = new Thickness(0, 8, 0, 0) }.Styled("Bar");
            Grid.SetRow(_progressBar, 1);
            Grid.SetColumn(_progressBar, 1);
            Grid.SetColumnSpan(_progressBar, 2);
            grid.Children.Add(_progressBar);
        }

        var row = new Border { Background = Brushes.Transparent, Child = grid };
        if (Busy)
        {
            if (!loadingThis) row.Opacity = 0.45;
        }
        else
        {
            row.Cursor = Cursors.Hand;
            row.MouseLeftButtonUp += (_, _) => Select(model);
        }
        return row;
    }

    private static Grid SelectionMark(bool selected)
    {
        var mark = new Grid { Width = 18, Height = 18, Margin = new Thickness(2, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        if (selected)
        {
            mark.Children.Add(new Ellipse().Themed(Shape.FillProperty, "AccentBrush"));
            mark.Children.Add(new Ellipse { Width = 6, Height = 6, Fill = Brushes.White });
        }
        else
        {
            mark.Children.Add(new Ellipse { StrokeThickness = 1.5 }.Themed(Shape.StrokeProperty, "TextSecondary"));
        }
        return mark;
    }

    private void Select(CotabbyModel model)
    {
        if (_context.Settings.ActiveModelId == model.Id) return;
        _context.Change(s => s.ActiveModelId = model.Id);
        if (ModelDownloader.IsCached(model)) Load(model);
    }

    /// <summary>Makes the model the active one, downloading it first when it is not on disk.</summary>
    private async void Load(CotabbyModel model)
    {
        if (Busy) return;
        if (_context.Settings.ActiveModelId != model.Id) _context.Change(s => s.ActiveModelId = model.Id);

        _loading = new CancellationTokenSource();
        _progress = null;
        _error = null;
        Rebuild();

        var progress = new Progress<ModelDownloader.Progress>(update =>
        {
            _progress = update;
            ShowProgress();
        });
        try
        {
            await _context.Host.EnsureModelReadyAsync(progress, _loading.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _error = ex.Message;
        }
        finally
        {
            _loading = null;
            // The runtime status rows read the new state on this refresh.
            _context.Refresh();
        }
    }

    private void ShowProgress()
    {
        if (_progressLabel is null || _progressBar is null) return;
        // A model that is already on disk loads without reporting progress, so the bar stays hidden.
        bool hasTotal = _progress?.TotalBytes > 0;
        _progressBar.Visibility = hasTotal ? Visibility.Visible : Visibility.Collapsed;
        if (_progress is null)
        {
            _progressLabel.Text = "Preparing…";
        }
        else if (hasTotal)
        {
            long total = _progress.TotalBytes!.Value;
            _progressBar.Value = (double)_progress.BytesDownloaded / total;
            _progressLabel.Text = $"{_progress.BytesDownloaded * 100 / total}%";
        }
        else
        {
            _progressLabel.Text = $"{_progress.BytesDownloaded / 1024 / 1024:N0} MB";
        }
    }
}
