using System.IO;
using System.Windows;
using System.Windows.Controls;
using Cotabby.App.UI.Controls;
using Cotabby.Core.History;
using Microsoft.Win32;

namespace Cotabby.App.UI.Settings.Panes;

/// <summary>
/// The Typing History card. The switches and exclusions are ordinary settings. Import and Delete
/// All go to <see cref="TypingHistoryStore"/>, which owns the archive and recording.
/// </summary>
internal static class TypingHistorySection
{
    public static SettingsCard Build(Rows r)
    {
        var context = r.Context;
        var store = context.Host.TypingHistory;

        // The store changes off the UI thread, for example when an import finishes.
        void OnStoreChanged() => Application.Current?.Dispatcher.InvokeAsync(context.Refresh);
        store.Changed += OnStoreChanged;
        context.Closed += () => store.Changed -= OnStoreChanged;

        return new SettingsCard("Typing History",
            r.Toggle("Use My Typing History",
                "Finishes phrases you often type and shows the model examples of how you write. "
                + "Only the local model uses it; it is never sent to an endpoint.",
                s => s.TypingHistoryEnabled, (s, v) => s.TypingHistoryEnabled = v)
                .Tagged("history personalize personalization learn phrases my writing"),
            r.Toggle("Record What I Type",
                "Saves the text of fields where Cotabby is active, encrypted on this PC. "
                + "Password fields, terminals, and disabled apps are never recorded.",
                s => s.TypingHistoryRecording, (s, v) => s.TypingHistoryRecording = v)
                .EnabledWhen(_ => store.Status == TypingHistoryStatus.Ready)
                .Tagged("history record privacy learn my writing"),
            Rows.Custom(Exclusions(context))
                .VisibleWhen(s => s.TypingHistoryExcludedApps.Count > 0),
            r.Stacked("Stored History", "", Manage(context, store))
                .Tagged("history typing history cotypist import delete erase exclude app privacy"));
    }

    private static StackPanel Exclusions(SettingsContext context)
    {
        var panel = new StackPanel();
        context.OnRefresh(() =>
        {
            panel.Children.Clear();
            foreach (var app in context.Settings.TypingHistoryExcludedApps)
            {
                if (panel.Children.Count > 0) panel.Children.Add(Ui.Separator());
                var remove = Ui.Button("Remove", () => context.Change(s => s.TypingHistoryExcludedApps.Remove(app)));
                DockPanel.SetDock(remove, Dock.Right);
                var name = Ui.Text($"Not recorded: {app}");
                name.VerticalAlignment = VerticalAlignment.Center;
                panel.Children.Add(new DockPanel { MinHeight = 40, Children = { remove, name } });
            }
        });
        return panel;
    }

    private static StackPanel Manage(SettingsContext context, TypingHistoryStore store)
    {
        var count = Ui.Text("");
        var message = Ui.Text("", "Caption");
        message.TextWrapping = TextWrapping.Wrap;
        message.Margin = new Thickness(0, 2, 0, 0);
        var exclude = Ui.Button("Exclude an App…", () => ChooseAppToExclude(context));
        var import = Ui.Button("Import Cotypist Export…", () => ChooseExportToImport(store));
        var deleteAll = Ui.Button("Delete All…", () => ConfirmDeleteAll(store));
        var buttons = Ui.Across(exclude, import, deleteAll);
        buttons.Margin = new Thickness(0, 10, 0, 0);

        context.OnRefresh(() =>
        {
            var status = store.Status;
            int records = store.RecordCount;
            count.Text = status switch
            {
                TypingHistoryStatus.Loading => "Opening typing history…",
                TypingHistoryStatus.Unavailable => "Stored history can't be read",
                _ => records == 1 ? "1 entry stored" : $"{records:N0} entries stored",
            };
            var (text, isError) = StatusMessage(store);
            message.Text = text ?? "";
            message.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
            if (isError) message.SetResourceReference(TextBlock.ForegroundProperty, "OrangeBrush");
            else message.ClearValue(TextBlock.ForegroundProperty);
            import.IsEnabled = status == TypingHistoryStatus.Ready && !store.IsImporting;
            deleteAll.IsEnabled = CanDeleteAll(store);
        });
        return new StackPanel { Children = { count, message, buttons } };
    }

    /// <summary>
    /// Delete All also clears an archive that can no longer be opened, and stays available after a
    /// failed deletion so it can be retried. Never during an import, which would add entries back.
    /// </summary>
    private static bool CanDeleteAll(TypingHistoryStore store)
    {
        if (store.IsImporting) return false;
        if (store.Status == TypingHistoryStatus.Unavailable) return true;
        return store.RecordCount > 0 || store.DeletionError is not null;
    }

    private static (string? Text, bool IsError) StatusMessage(TypingHistoryStore store)
    {
        if (store.DeletionError is { } deletionError) return (deletionError, true);
        if (store.Status == TypingHistoryStatus.Unavailable) return (store.UnavailableMessage, true);
        if (store.IsImporting) return ("Importing…", false);
        return (store.LastImportMessage, false);
    }

    private static void ConfirmDeleteAll(TypingHistoryStore store)
    {
        int records = store.RecordCount;
        var answer = MessageBox.Show(
            "This removes every recorded and imported entry and its encryption key. It can't be undone.",
            records > 0 ? $"Delete all {records:N0} typing history entries?" : "Delete all typing history?",
            MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
        if (answer == MessageBoxResult.OK) store.DeleteAll();
    }

    /// <summary>Asks for user_inputs.json from a decrypted Cotypist export.</summary>
    private static void ChooseExportToImport(TypingHistoryStore store)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import Cotypist Export: choose user_inputs.json",
            Filter = "JSON files (*.json)|*.json",
            Multiselect = false,
        };
        if (dialog.ShowDialog() != true) return;
        _ = store.ImportCotypistExportAsync(dialog.FileName);
    }

    /// <summary>Asks for an app's executable. Recording is excluded by its process name.</summary>
    private static void ChooseAppToExclude(SettingsContext context)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Don't Record in an App",
            Filter = "Apps (*.exe)|*.exe",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Multiselect = false,
        };
        if (dialog.ShowDialog() != true) return;
        string processName = Path.GetFileNameWithoutExtension(dialog.FileName);
        if (context.Settings.TypingHistoryExcludedApps.Contains(processName, StringComparer.OrdinalIgnoreCase)) return;
        context.Change(s => s.TypingHistoryExcludedApps.Add(processName));
    }
}
