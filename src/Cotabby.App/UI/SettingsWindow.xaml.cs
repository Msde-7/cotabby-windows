using System.Windows;
using Cotabby.App.Hosting;
using Cotabby.Core.Models;
using Cotabby.Inference;

namespace Cotabby.App.UI;

/// <summary>
/// Single-page settings window. The macOS port has a multi-tab settings UI with
/// onboarding, runtime flags, and developer toggles; we ship a single-purpose
/// model picker for the MVP — the rest can be added incrementally.
/// </summary>
public partial class SettingsWindow : Window
{
    private static SettingsWindow? _instance;
    private readonly AppHost _host;
    private CancellationTokenSource? _downloadCts;

    private sealed record ModelRow(string Id, string DisplayName, string SizeDisplay);

    public SettingsWindow(AppHost host)
    {
        _host = host;
        InitializeComponent();

        var rows = ModelCatalog.All
            .Select(m => new ModelRow(
                m.Id,
                m.DisplayName,
                $"{m.ApproxSizeBytes / 1024.0 / 1024.0:N0} MB" +
                    (ModelDownloader.IsCached(m) ? " · downloaded" : "")))
            .ToList();
        ModelList.ItemsSource = rows;

        var activeId = host.Settings.ActiveModelId ?? ModelCatalog.All[0].Id;
        ModelList.SelectedItem = rows.FirstOrDefault(r => r.Id == activeId) ?? rows[0];

        StatusText.Text = host.Runtime.IsReady
            ? $"Loaded: {host.Runtime.ActiveModel?.DisplayName}"
            : "No model loaded.";

        Closed += (_, _) =>
        {
            _downloadCts?.Cancel();
            _instance = null;
        };
    }

    public static void ShowOrFocus(AppHost host)
    {
        if (_instance is not null)
        {
            _instance.Activate();
            return;
        }
        _instance = new SettingsWindow(host);
        _instance.Show();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private async void OnUse(object sender, RoutedEventArgs e)
    {
        if (ModelList.SelectedItem is not ModelRow row) return;
        var model = ModelCatalog.FindById(row.Id);
        if (model is null) return;

        UseButton.IsEnabled = false;
        ModelList.IsEnabled = false;
        DownloadProgress.Visibility = Visibility.Visible;
        DownloadProgress.Value = 0;
        StatusText.Text = "Preparing…";

        _downloadCts = new CancellationTokenSource();
        var progress = new Progress<ModelDownloader.Progress>(p =>
        {
            if (p.TotalBytes is { } total && total > 0)
            {
                DownloadProgress.Value = (double)p.BytesDownloaded / total;
                StatusText.Text = $"Downloading… {p.BytesDownloaded / 1024 / 1024:N0} / {total / 1024 / 1024:N0} MB";
            }
            else
            {
                StatusText.Text = $"Downloading… {p.BytesDownloaded / 1024 / 1024:N0} MB";
            }
        });

        try
        {
            _host.Settings.ActiveModelId = model.Id;
            _host.PersistSettings();

            await _host.EnsureModelReadyAsync(progress, _downloadCts.Token);
            StatusText.Text = $"Ready · {model.DisplayName}";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Cancelled.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Failed: " + ex.Message;
        }
        finally
        {
            DownloadProgress.Visibility = Visibility.Collapsed;
            UseButton.IsEnabled = true;
            ModelList.IsEnabled = true;
        }
    }
}
