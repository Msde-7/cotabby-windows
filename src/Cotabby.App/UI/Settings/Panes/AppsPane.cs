using System.Windows;
using System.Windows.Controls;
using Cotabby.App.UI.Controls;

namespace Cotabby.App.UI.Settings.Panes;

internal static class AppsPane
{
    public const string ProcessNameHint = "Process name (no .exe)";

    public static SettingsPane Build(Rows r) => new("Apps", "\uE71D", "Where Cotabby stays quiet",
        new SettingsCard("Disabled Apps",
            Rows.Note("Cotabby won't autocomplete in these apps."),
            Rows.Custom(BlockedApps(r.Context)),
            r.Stacked("Disable an App", "Enter the process name of the app, such as notepad.",
                Ui.AddBox(ProcessNameHint, name => Block(r.Context, name)))
                .Tagged("disabled apps exclude block ignore blacklist deny list exception skip off in")),
        new SettingsCard("Terminals",
            r.Toggle("Suggest in Integrated Terminals",
                "Show ghost text in VS Code and Cursor integrated terminals. Off by default so suggestions "
                + "stay out of shell prompts; the editor and chat in the same window keep suggesting either way.",
                s => s.SuggestInIntegratedTerminals, (s, v) => s.SuggestInIntegratedTerminals = v)
                .Tagged("vscode vs code cursor shell xterm command line cli console"),
            Rows.Note("Terminal apps such as Windows Terminal, PowerShell and Command Prompt are always excluded.")));

    /// <summary>The process name as Windows reports it, without the extension people tend to type.</summary>
    public static string ProcessName(string typed) =>
        typed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? typed[..^4] : typed;

    private static void Block(SettingsContext context, string name)
    {
        var processName = ProcessName(name);
        if (context.Settings.BlockedApps.Contains(processName, StringComparer.OrdinalIgnoreCase)) return;
        context.Change(s => s.BlockedApps.Add(processName));
    }

    private static StackPanel BlockedApps(SettingsContext context)
    {
        var panel = new StackPanel();
        context.OnRefresh(() =>
        {
            panel.Children.Clear();
            foreach (var app in context.Settings.BlockedApps)
            {
                if (panel.Children.Count > 0) panel.Children.Add(Ui.Separator());
                var remove = Ui.Button("Remove", () => context.Change(s => s.BlockedApps.Remove(app)));
                DockPanel.SetDock(remove, Dock.Right);
                var name = Ui.Text(app);
                panel.Children.Add(new DockPanel { MinHeight = 40, Children = { remove, name } });
            }
            if (panel.Children.Count == 0)
            {
                var empty = Ui.Text("No apps are disabled. Cotabby is active in every supported field.", "Caption");
                empty.Margin = new Thickness(0, 12, 0, 12);
                panel.Children.Add(empty);
            }
        });
        return panel;
    }
}
