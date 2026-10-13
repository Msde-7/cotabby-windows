using System.Windows;
using System.Windows.Controls;
using Cotabby.App.UI.Controls;

namespace Cotabby.App.UI.Settings.Panes;

internal static class AboutPane
{
    private const string WindowsRepository = "https://github.com/Msde-7/cotabby-windows";
    private const string UpstreamRepository = "https://github.com/FuJacob/cotabby";

    private static readonly (string Name, string Role)[] Acknowledgements =
    [
        ("llama.cpp", "Runs the local models."),
        ("LLamaSharp", "Connects Cotabby to llama.cpp."),
        ("gemoji", "The emoji names and keywords behind the emoji picker."),
        ("SymSpell", "Fast spelling correction."),
        ("H.NotifyIcon", "The system tray icon."),
    ];

    public static SettingsPane Build(Rows r) => new("About", "\uE946", "Version, source, and licenses",
        new SettingsCard(null, Rows.Custom(Branding.Identity("Local Windows AI Autocomplete"))),
        new SettingsCard("Resources",
            r.Action("Cotabby for Windows", WindowsRepository, "Open", () => Ui.OpenUrl(WindowsRepository))
                .Tagged("github repo repository source code git issues contribute open source"),
            r.Action("Cotabby for macOS", "The original app this is a port of. " + UpstreamRepository, "Open",
                () => Ui.OpenUrl(UpstreamRepository))
                .Tagged("github repo repository source code upstream mac original"),
            r.Labeled("License", "Cotabby is free and open source software.", Ui.Text("GNU AGPL v3.0", "Caption"))
                .Tagged("licence agpl gnu open source legal")),
        new SettingsCard(null,
            r.Stacked("Acknowledgements", "Third-party packages Cotabby ships with.", Credits())
                .Tagged("licenses credits thanks attribution third party notices open source")));

    private static StackPanel Credits()
    {
        var panel = new StackPanel();
        foreach (var (name, role) in Acknowledgements)
        {
            var title = new TextBlock { Text = name, FontSize = 12, FontWeight = FontWeights.SemiBold, Width = 110 };
            var line = Ui.Across(title, Ui.Text(role, "Caption"));
            line.Margin = new Thickness(0, 0, 0, 6);
            panel.Children.Add(line);
        }
        return panel;
    }
}
