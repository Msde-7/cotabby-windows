using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Cotabby.App.Hosting;
using Cotabby.App.UI.Controls;
using Cotabby.Core.Models;
using Cotabby.Inference;
using Microsoft.Win32;

namespace Cotabby.App.UI.Settings.Panes;

/// <summary>
/// The downloadable models with their download, load and active state. Clicking a model
/// that is on disk switches to it. One that is not needs its Download button, so a stray
/// click cannot start a download of several gigabytes. A download can be paused, which keeps
/// what has arrived, or cancelled, which deletes it. A model on disk can be deleted, and the
/// user can copy in a GGUF file of their own.
/// </summary>
internal sealed class ModelList : StackPanel
{
    /// <summary>Every GGUF file starts with these bytes.</summary>
    private static readonly byte[] GgufMagic = "GGUF"u8.ToArray();

    private readonly SettingsContext _context;
    private CancellationTokenSource? _loading;
    private ModelDownloader.Progress? _progress;
    private bool _downloading;
    private bool _cancelRequested;
    private bool _copying;
    private string? _error;
    private TextBlock? _status;
    private ProgressBar? _progressBar;

    public ModelList(SettingsContext context)
    {
        _context = context;
        context.Closed += () => _loading?.Cancel();
        context.OnRefreshWhileVisible(this, Rebuild);
    }

    private bool Busy => _loading is not null || _copying;

    private void Rebuild()
    {
        Children.Clear();
        _status = null;
        _progressBar = null;

        // With power switching on, this is the model of the current power source.
        var selectedId = _context.Host.SelectedModelId;
        foreach (var model in ModelCatalog.Available())
        {
            if (Children.Count > 0) Children.Add(Ui.Separator());
            Children.Add(Row(model, model.Id == selectedId));
        }
        if (_error is not null)
        {
            var error = Ui.Text(_error, "Caption").Themed(TextBlock.ForegroundProperty, "OrangeBrush");
            error.Margin = new Thickness(0, 4, 0, 10);
            Children.Add(error);
        }
        Children.Add(Footer());
        ShowProgress();
    }

    /// <summary>Upstream's Add Your Own and Refresh buttons under the list.</summary>
    private StackPanel Footer()
    {
        var add = Ui.Button("Add Your Own…", AddOwn);
        add.ToolTip = "Copy a GGUF model file into the models folder.";
        var refresh = Ui.Button("Refresh", _context.Refresh);
        refresh.ToolTip = "Look for model files added to the models folder.";
        add.IsEnabled = refresh.IsEnabled = !Busy;
        var footer = _copying
            ? Ui.Across(add, refresh, Ui.Text("Copying into the models folder…", "Caption"))
            : Ui.Across(add, refresh);
        footer.Margin = new Thickness(0, 4, 0, 10);
        return footer;
    }

    private Border Row(CotabbyModel model, bool selected)
    {
        var runtime = _context.Host.Runtime;
        bool loadingThis = Busy && selected;
        bool downloadingThis = loadingThis && _downloading;
        // A partial left by an earlier download shows as paused, also after a restart.
        var onDisk = loadingThis ? null : ModelDownloader.StateOnDisk(model);
        var paused = onDisk as ModelDownloadState.Paused;
        // The first-run window may be downloading this model. That download outlives the window,
        // so it is paused or cancelled here.
        bool downloadingElsewhere = onDisk is ModelDownloadState.Downloading;
        var setupDownload = _context.Host.RunningSetupDownload is { } running && running.ModelId == model.Id ? running : null;

        var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        names.Children.Add(new TextBlock { Text = model.DisplayName, FontSize = 13, FontWeight = FontWeights.SemiBold });
        var subtitle = Ui.Text(model.Subtitle, "Caption");
        subtitle.Margin = new Thickness(0, 2, 0, 0);
        if (downloadingThis) _status = subtitle;
        if (downloadingElsewhere) subtitle.Text = onDisk!.StatusText;
        if (paused is not null)
        {
            subtitle.Text = paused.StatusText;
            subtitle.Themed(TextBlock.ForegroundProperty, "OrangeBrush");
        }
        names.Children.Add(subtitle);
        Grid.SetColumn(names, 1);

        FrameworkElement trailing;
        if (downloadingThis)
        {
            trailing = Ui.Across(Ui.Button("Pause", Pause), Ui.Button("Cancel", CancelDownload));
        }
        else if (loadingThis)
        {
            trailing = Ui.Text("Preparing…", "Caption");
        }
        else if (runtime.IsReady && runtime.ActiveModel?.Id == model.Id)
        {
            // Not always the selected one: power switching falls back to it while the pick is not on disk.
            var check = Ui.Icon("\uE73E", 12);
            var active = new TextBlock { Text = "Active", FontSize = 12, FontWeight = FontWeights.SemiBold };
            check.Themed(TextBlock.ForegroundProperty, "GreenBrush");
            active.Themed(TextBlock.ForegroundProperty, "GreenBrush");
            trailing = Ui.Across(check, active);
        }
        else if (downloadingElsewhere)
        {
            trailing = setupDownload is not null
                ? Ui.Across(
                    Ui.Button("Pause", () => StopSetupDownload(setupDownload, model, discard: false)),
                    Ui.Button("Cancel", () => StopSetupDownload(setupDownload, model, discard: true)))
                : Ui.Text("In setup window", "Caption");
        }
        else if (paused is not null)
        {
            trailing = Ui.Across(Ui.Button("Resume", () => Load(model)), Ui.Button("Cancel", () => Discard(model)));
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
        if (!loadingThis && !downloadingElsewhere && ModelDownloader.IsCached(model))
        {
            trailing = Ui.Across(trailing, DeleteButton(model));
        }
        trailing.VerticalAlignment = VerticalAlignment.Center;
        trailing.IsEnabled = !Busy || loadingThis;
        Grid.SetColumn(trailing, 2);

        var grid = new Grid { Margin = new Thickness(0, 10, 0, 10), Children = { SelectionMark(selected), names, trailing } };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition());
        if (downloadingThis || paused is not null)
        {
            var bar = new ProgressBar { Maximum = 1, Margin = new Thickness(0, 8, 0, 0) }.Styled("Bar");
            Grid.SetRow(bar, 1);
            Grid.SetColumn(bar, 1);
            Grid.SetColumnSpan(bar, 2);
            grid.Children.Add(bar);
            if (downloadingThis) _progressBar = bar;
            else
            {
                // A grey bar reads as stopped rather than active.
                bar.Value = paused!.ProgressFraction ?? 0;
                bar.Themed(Control.ForegroundProperty, "TextSecondary");
            }
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
        if (_context.Host.SelectedModelId == model.Id) return;
        _context.Change(s => AppHost.PickModel(s, model.Id));
        if (ModelDownloader.IsCached(model)) Load(model);
    }

    /// <summary>Makes the model the active one, downloading it first when it is not on disk.</summary>
    private async void Load(CotabbyModel model)
    {
        if (Busy) return;
        if (_context.Host.SelectedModelId != model.Id) _context.Change(s => AppHost.PickModel(s, model.Id));

        var loading = _loading = new CancellationTokenSource();
        _downloading = !ModelDownloader.IsCached(model);
        _cancelRequested = false;
        _progress = null;
        _error = null;
        Rebuild();

        var progress = new Progress<ModelDownloader.Progress>(update =>
        {
            if (!_downloading) return;
            _progress = update;
            ShowProgress();
        });
        var work = DownloadAndLoadAsync(model, progress, loading.Token);
        // The first-run window asking for the same model follows this download, as it follows its
        // own, where starting a second one would fail.
        var shared = _downloading ? new AppHost.SetupDownload(model.Id, loading, work, progress) : null;
        if (shared is not null) _context.Host.RunningSetupDownload = shared;
        try
        {
            await work;
        }
        catch (OperationCanceledException)
        {
            // Pause keeps the partial file. Cancel deletes it once the download has stopped.
            if (_cancelRequested) Discard(model);
        }
        catch (Exception ex)
        {
            _error = "Could not prepare the model. " + ex.Message;
        }
        finally
        {
            _loading = null;
            if (shared is not null && ReferenceEquals(_context.Host.RunningSetupDownload, shared))
            {
                _context.Host.RunningSetupDownload = null;
            }
            // The runtime status rows read the new state on this refresh.
            _context.Refresh();
        }
    }

    private async Task DownloadAndLoadAsync(CotabbyModel model, IProgress<ModelDownloader.Progress> progress, CancellationToken ct)
    {
        if (_downloading)
        {
            await _context.Host.Downloader.DownloadAsync(model, progress, ct);
            // Loading cannot be paused or cancelled, so those buttons go once the file is in place.
            _downloading = false;
            _progress = null;
            Rebuild();
        }
        await _context.Host.EnsureModelReadyAsync(null, ct);
    }

    private void Pause() => _loading?.Cancel();

    private void CancelDownload()
    {
        _cancelRequested = true;
        _loading?.Cancel();
    }

    /// <summary>Stops the first-run window's download. Pause keeps what has arrived, cancel deletes it.</summary>
    private async void StopSetupDownload(AppHost.SetupDownload download, CotabbyModel model, bool discard)
    {
        download.Cancellation.Cancel();
        try
        {
            await download.Completion;
        }
        catch (Exception)
        {
            // The first-run window, if it is still open, shows how its download ended.
        }
        if (discard) Discard(model);
        _context.Refresh();
    }

    private void Discard(CotabbyModel model)
    {
        try
        {
            ModelDownloader.DiscardPartial(model);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _error = "Could not discard the download. " + ex.Message;
        }
        if (!Busy) Rebuild();
    }

    private Button DeleteButton(CotabbyModel model)
    {
        var button = new Button { Content = Ui.Icon("\uE74D", 12), ToolTip = "Delete this model file" }.Styled("Btn");
        button.Click += (_, _) => Delete(model);
        return button;
    }

    /// <summary>
    /// Deletes a model file after the user confirms. A loaded model is unloaded first, since
    /// Windows cannot delete a file the runtime still has mapped.
    /// </summary>
    private async void Delete(CotabbyModel model)
    {
        if (Busy) return;
        bool loaded = _context.Host.Runtime.ActiveModel?.Id == model.Id;
        string message =
            $"{model.DisplayName} ({model.ApproxSizeBytes / 1e9:0.0} GB) will be removed from the models folder. "
            + (model.IsCustom
                ? "Cotabby can't download your own model again, so keep a copy if you may want it back."
                : "You can download it again later.")
            + (loaded ? "\n\nIt is loaded now. Cotabby unloads it first, so local suggestions stop until you load another model." : "");
        var answer = MessageBox.Show(
            message, $"Delete {model.DisplayName}?",
            MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
        if (answer != MessageBoxResult.OK) return;

        _error = null;
        try
        {
            if (loaded) await _context.Host.UnloadModelAsync();
            File.Delete(ModelDownloader.LocalPath(model));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _error = "Could not delete the model. " + ex.Message;
        }
        _context.Refresh();
    }

    /// <summary>
    /// Copies GGUF files the user picks into the models folder, where the list finds them, as
    /// upstream's Add Your Own does. A copy leaves the original where it was.
    /// </summary>
    private async void AddOwn()
    {
        if (Busy) return;
        var dialog = new OpenFileDialog
        {
            Title = "Add your own models",
            Filter = "GGUF models (*.gguf)|*.gguf",
            Multiselect = true,
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

        var folder = ModelCatalog.DefaultLocalDirectory();
        var problems = new List<string>();
        _copying = true;
        _error = null;
        Rebuild();
        foreach (var source in dialog.FileNames)
        {
            var name = System.IO.Path.GetFileName(source);
            var target = System.IO.Path.Combine(folder, name);
            if (File.Exists(target))
            {
                problems.Add($"{name} is already in the models folder.");
                continue;
            }
            if (!IsGguf(source))
            {
                problems.Add($"{name} is not a GGUF model file.");
                continue;
            }
            // Copying under another name keeps a half-copied file out of the model list.
            var partial = target + ".copying";
            try
            {
                await Task.Run(() =>
                {
                    Directory.CreateDirectory(folder);
                    File.Copy(source, partial, overwrite: true);
                    File.Move(partial, target);
                });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                problems.Add($"Could not copy {name}. {ex.Message}");
                DeleteQuietly(partial);
            }
        }
        _copying = false;
        _error = problems.Count > 0 ? string.Join("\n", problems) : null;
        _context.Refresh();
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover partial copy is harmless. The list ignores it and the next copy replaces it.
        }
    }

    private static bool IsGguf(string path)
    {
        try
        {
            using var file = File.OpenRead(path);
            Span<byte> header = stackalloc byte[4];
            return file.Read(header) == header.Length && header.SequenceEqual(GgufMagic);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private void ShowProgress()
    {
        if (_status is null || _progressBar is null) return;
        var state = _progress is null ? null : ModelDownloadState.From(_progress);
        _status.Text = state?.StatusText ?? "Preparing download…";
        _progressBar.Visibility = Ui.Shown(state?.ProgressFraction is not null);
        _progressBar.Value = state?.ProgressFraction ?? 0;
    }
}
